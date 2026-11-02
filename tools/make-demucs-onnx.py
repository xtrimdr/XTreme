"""Turns the Demucs checkpoints into the ONNX files of "Separar en stems" (StemSeparator).

Only the network goes in the graph. The STFT and its inverse stay out, in C#: ONNX cannot carry them well. Each file
takes one segment of audio ("mix", 1x2xL) and its spectrogram as channels ("mag", 1x4x2048xT), and returns both
branches ("spec", 1xSx4x2048xT and "wave", 1xSx2xL). Weights go as float16, which is how Meta stores them, so nothing
is lost and the files keep the size of the checkpoints. Each file is checked against PyTorch before it is kept.

Run it through tools/get-demucs.ps1.
"""
import sys
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
import torch
from einops import rearrange
from demucs.pretrained import get_model
from onnx import TensorProto, helper, numpy_helper

# output file -> (Demucs name, index in its bag). The one for "other" is not needed: that stem is the mix minus the rest.
MODELS = {
    "htdemucs": ("htdemucs", 0),
    "htdemucs_6s": ("htdemucs_6s", 0),
    "htdemucs_ft_drums": ("htdemucs_ft", 0),
    "htdemucs_ft_bass": ("htdemucs_ft", 1),
    "htdemucs_ft_vocals": ("htdemucs_ft", 3),
}
# Below this many values an initializer stays float32: small ones are constants of the graph, not trained weights.
MIN_FLOAT16_SIZE = 1024
# The largest difference with PyTorch accepted on the waveform, over noise at 0.1.
MAX_ERROR = 1e-2


class Core(torch.nn.Module):
    """HTDemucs.forward without the transforms and the masking, which is a reshape in this model."""

    def __init__(self, m):
        super().__init__()
        self.m = m

    def forward(self, mix, mag):
        m = self.m
        x = mag
        B, C, Fq, T = x.shape
        mean = x.mean(dim=(1, 2, 3), keepdim=True)
        std = x.std(dim=(1, 2, 3), keepdim=True)
        x = (x - mean) / (1e-5 + std)
        xt = mix
        meant = xt.mean(dim=(1, 2), keepdim=True)
        stdt = xt.std(dim=(1, 2), keepdim=True)
        xt = (xt - meant) / (1e-5 + stdt)
        saved, saved_t, lengths, lengths_t = [], [], [], []
        for idx, encode in enumerate(m.encoder):
            lengths.append(x.shape[-1])
            inject = None
            if idx < len(m.tencoder):
                lengths_t.append(xt.shape[-1])
                tenc = m.tencoder[idx]
                xt = tenc(xt)
                if not tenc.empty:
                    saved_t.append(xt)
                else:
                    inject = xt
            x = encode(x, inject)
            if idx == 0 and m.freq_emb is not None:
                frs = torch.arange(x.shape[-2], device=x.device)
                emb = m.freq_emb(frs).t()[None, :, :, None].expand_as(x)
                x = x + m.freq_emb_scale * emb
            saved.append(x)
        if m.crosstransformer:
            if m.bottom_channels:
                b, c, f, t = x.shape
                x = rearrange(x, "b c f t-> b c (f t)")
                x = m.channel_upsampler(x)
                x = rearrange(x, "b c (f t)-> b c f t", f=f)
                xt = m.channel_upsampler_t(xt)
            x, xt = m.crosstransformer(x, xt)
            if m.bottom_channels:
                x = rearrange(x, "b c f t-> b c (f t)")
                x = m.channel_downsampler(x)
                x = rearrange(x, "b c (f t)-> b c f t", f=f)
                xt = m.channel_downsampler_t(xt)
        for idx, decode in enumerate(m.decoder):
            skip = saved.pop(-1)
            x, pre = decode(x, skip, lengths.pop(-1))
            offset = m.depth - len(m.tdecoder)
            if idx >= offset:
                tdec = m.tdecoder[idx - offset]
                length_t = lengths_t.pop(-1)
                if tdec.empty:
                    pre = pre[:, :, 0]
                    xt, _ = tdec(pre, None, length_t)
                else:
                    skip = saved_t.pop(-1)
                    xt, _ = tdec(xt, skip, length_t)
        S = len(m.sources)
        L = mix.shape[-1]
        x = x.view(B, S, -1, Fq, T)
        x = x * std[:, None] + mean[:, None]
        xt = xt.view(B, S, -1, L)
        xt = xt * stdt[:, None] + meant[:, None]
        return x, xt


def to_float16(path):
    """Stores the large initializers as float16, each with a Cast back at the start of the graph."""
    model = onnx.load(path)
    graph = model.graph
    casts = []
    for init in graph.initializer:
        if init.data_type != TensorProto.FLOAT or np.prod(init.dims) < MIN_FLOAT16_SIZE:
            continue
        name = init.name
        half = numpy_helper.from_array(numpy_helper.to_array(init).astype(np.float16), name + "_half")
        init.CopyFrom(half)
        casts.append(helper.make_node("Cast", [name + "_half"], [name], to=TensorProto.FLOAT))
    for node in reversed(casts):
        graph.node.insert(0, node)
    # DirectML fuses each Sigmoid with the next op and the audio comes out wrong. An op whose output is also an output of
    # the graph can't be fused, and nothing has to read it: this costs nothing, on the GPU or on the CPU.
    for node in graph.node:
        if node.op_type == "Sigmoid":
            graph.output.append(helper.make_tensor_value_info(node.output[0], TensorProto.FLOAT, None))
    onnx.save(model, path)


def export(out, name, index):
    m = get_model(name).models[index].eval()
    L = int(m.segment * m.samplerate)
    torch.manual_seed(1)
    mix = torch.randn(1, 2, L) * 0.1
    with torch.no_grad():
        mag = m._magnitude(m._spec(mix))
        core = Core(m).eval()
        spec, wave = core(mix, mag)
        torch.onnx.export(core, (mix, mag), str(out), opset_version=17, dynamo=False,
                          input_names=["mix", "mag"], output_names=["spec", "wave"])
    to_float16(str(out))
    session = ort.InferenceSession(str(out), providers=["CPUExecutionProvider"])
    got_spec, got_wave = session.run(["spec", "wave"], {"mix": mix.numpy(), "mag": mag.numpy()})
    error = float(np.abs(got_wave - wave.numpy()).max())
    if error > MAX_ERROR:
        out.unlink()
        raise SystemExit(f"{out.name}: difiere de PyTorch ({error:.2e})")
    print(f"{out.name}: {out.stat().st_size / 1e6:.0f} MB, diferencia con PyTorch {error:.1e}", flush=True)


def main():
    dest = Path(sys.argv[1])
    dest.mkdir(parents=True, exist_ok=True)
    torch.backends.mha.set_fastpath_enabled(False)  # the native attention op has no ONNX export
    for file, (name, index) in MODELS.items():
        out = dest / f"{file}.onnx"
        if not out.exists():
            export(out, name, index)


if __name__ == "__main__":
    main()
