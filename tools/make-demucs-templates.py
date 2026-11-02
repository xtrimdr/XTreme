"""Strips the Demucs weights from the ONNX files of tools/get-demucs.ps1, so the graphs alone can ship with the app.

Every initializer that exists in Meta's checkpoint moves out to external data, and a line in <name>.map says which entry
of the checkpoint fills it, and how (some were sliced or computed by the export): the app downloads the .th to the user's PC and writes <name>.onnx.data from it
(StemConverter). The checkpoints are matched by content, not parsed, so no weight stays here; what is left is what the
export computed by itself, which is listed for review.

Usage: make-demucs-templates.py <folder with the .onnx> <folder with the .th> <output folder>
"""
import hashlib
import sys
import zipfile
from pathlib import Path

import numpy as np
import onnx
from onnx import TensorProto, numpy_helper

CHECKPOINTS = {
    "htdemucs": "955717e8-8726e21a.th",
    "htdemucs_6s": "5c90dfd2-34c22ccb.th",
    "htdemucs_ft_drums": "f7e0c4bc-ba3fe64a.th",
    "htdemucs_ft_bass": "d12395a8-e57c48e6.th",
    "htdemucs_ft_vocals": "04573f0d-f3cf25b2.th",
}
TYPES = {"f2": np.float16, "f4": np.float32}
# HTDemucs' freq_emb (emb_scale) and freq_emb_scale, in its default arguments.
EMBEDDING_SCALE = 10
FREQ_EMB_SCALE = 0.2


def load_entries(path):
    """Every tensor of the checkpoint: entry name -> (kind, float32 values), trusting the sizes the entries have."""
    table = {}
    with zipfile.ZipFile(path) as z:
        for info in z.infolist():
            if "/data/" not in info.filename:
                continue
            raw = z.read(info)
            for kind, dtype in TYPES.items():
                if len(raw) % np.dtype(dtype).itemsize == 0:
                    table.setdefault(info.filename, []).append((kind, np.frombuffer(raw, dtype).astype(np.float32)))
    return table


def find(array, table):
    """How to rebuild this array from the checkpoint: (entry, kind, mode, a, b, start)."""
    values = array.astype(np.float32)
    flat = values.ravel()
    for entry, kinds in table.items():
        for kind, src in kinds:
            if flat.size == src.size and np.array_equal(src, flat):
                return entry, kind, "C", 0, 0, 0
    for entry, kinds in table.items():
        for kind, src in kinds:
            if values.ndim == 2 and src.size >= values.size and src.size % values.shape[0] == 0:
                a, b = values.shape
                rows = src.reshape(-1, a)
                for start in range(0, rows.shape[0] - b + 1, min(a, b)):
                    if np.array_equal(rows[start:start + b], values.T):
                        return entry, kind, "T", a, b, start
            if values.ndim == 1 and src.size > flat.size:
                for start in range(0, src.size - flat.size + 1, flat.size // 2 or 1):
                    if np.array_equal(src[start:start + flat.size], flat):
                        return entry, kind, "C", 0, 0, start
            if array.shape[:1] == (1,) and values.ndim == 4 and src.size == values.size:
                c, f = values.shape[1:3]
                made = (src.reshape(f, c) * np.float32(EMBEDDING_SCALE)).T
                for factor in (1, FREQ_EMB_SCALE):
                    if np.array_equal((made * np.float32(factor)).astype(array.dtype).ravel(), array.ravel()):
                        return entry, kind, "E", f, c, factor
    return None


def main():
    models, checkpoints, out = map(Path, sys.argv[1:4])
    out.mkdir(parents=True, exist_ok=True)
    for name, th in CHECKPOINTS.items():
        table = load_entries(checkpoints / th)
        model = onnx.load(models / f"{name}.onnx")
        lines, left, offset = [], [], 0
        digest = hashlib.sha256()
        for init in model.graph.initializer:
            if init.data_type not in (TensorProto.FLOAT, TensorProto.FLOAT16):
                continue
            array = numpy_helper.to_array(init)
            hit = find(array, table)
            if hit is None:
                if array.size > 1:
                    left.append((init.name, array.shape))
                continue
            raw = array.tobytes()
            digest.update(raw)
            entry, kind, mode, a, b, start = hit
            target = "f2" if init.data_type == TensorProto.FLOAT16 else "f4"
            lines.append(f"{entry.split('/data/')[1]} {kind} {target} {mode} {a} {b} {start} {offset} {len(raw)}")
            init.ClearField("raw_data")
            init.ClearField("float_data")
            init.ClearField("int32_data")
            init.data_location = TensorProto.EXTERNAL
            for key, value in (("location", f"{name}.onnx.data"), ("offset", str(offset)), ("length", str(len(raw)))):
                item = init.external_data.add()
                item.key, item.value = key, value
            offset += len(raw)
        onnx.save(model, out / f"{name}.onnx")
        (out / f"{name}.map").write_text("\n".join([f"{offset} {digest.hexdigest()}", *lines]) + "\n")
        size = (out / f"{name}.onnx").stat().st_size
        print(f"{name}: {len(lines)} pesos, {offset / 1e6:.0f} MB, grafo {size / 1e3:.0f} KB, sin coincidencia: {left[:12]} ({len(left)})")


if __name__ == "__main__":
    main()
