"""Renders the icon's record in Blender (tools/make-icon.ps1 runs it): a black vinyl with a giant label of spun
orange metal and a dark polished metal X on it. Transparent outside the disc.

    blender -b --factory-startup -P vinilo.py -- <output.png> <size>

The grooves are a procedural bump (concentric sine, with a few smooth gaps between "tracks"), and the surface is
anisotropic along them, which is what makes a real record catch light in bright wedges: four lights on the diagonals
turn those wedges into an X of light that echoes the one on the label. Top view, orthographic.
"""
import math
import os
import sys

import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
OUT, SIZE = args[0], int(args[1])

# Geometry, in record radii
DISC_THICK = 0.06
LABEL = 0.81
X_HALF, X_WIDTH = 0.38, 0.25
RING_R, RING_W = 0.695, 0.03
HOLE = 0.045
# Grooves: cycles per radius unit and bump strength; smooth gaps between tracks as (radius, half width)
GROOVES = 70
GROOVE_DEPTH = 0.0009
GAPS = ((0.885, 0.008), (0.935, 0.007), (0.982, 0.018))

scene = bpy.context.scene
for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)


def node(nt, kind, x, y, **props):
    n = nt.nodes.new(kind)
    n.location = (x, y)
    for k, v in props.items():
        setattr(n, k, v)
    return n


def link(nt, a, b):
    nt.links.new(a, b)


def set_input(bsdf, names, value):
    """Sets the first of these Principled inputs that exists (Blender renamed several between 3.x and 4.x)."""
    for name in names:
        if name in bsdf.inputs:
            bsdf.inputs[name].default_value = value
            return


def principled(name, color, rough, metal=0.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = (*color, 1)
    b.inputs["Metallic"].default_value = metal
    b.inputs["Roughness"].default_value = rough
    return m, b


def vinyl_material():
    m = bpy.data.materials.new("vinilo")
    m.use_nodes = True
    nt = m.node_tree
    nt.nodes.clear()
    out = node(nt, "ShaderNodeOutputMaterial", 1300, 0)
    bsdf = node(nt, "ShaderNodeBsdfPrincipled", 1000, 0)
    link(nt, bsdf.outputs["BSDF"], out.inputs["Surface"])
    bsdf.inputs["Base Color"].default_value = (0.012, 0.012, 0.014, 1)
    bsdf.inputs["Metallic"].default_value = 0.0
    set_input(bsdf, ("Specular IOR Level", "Specular"), 0.9)
    set_input(bsdf, ("Anisotropic",), 0.9)
    # The grooves run in circles around the centre: the tangent that anisotropy follows
    tan = node(nt, "ShaderNodeTangent", 700, -300, direction_type="RADIAL", axis="Z")
    link(nt, tan.outputs["Tangent"], bsdf.inputs["Tangent"])

    tc = node(nt, "ShaderNodeTexCoord", -1400, 0)
    length = node(nt, "ShaderNodeVectorMath", -1200, 0, operation="LENGTH")
    link(nt, tc.outputs["Object"], length.inputs[0])
    r = length.outputs["Value"]

    # 1 inside a smooth gap between tracks, 0 on the grooved part
    gap = None
    for k, (c, w) in enumerate(GAPS):
        sub = node(nt, "ShaderNodeMath", -1000, 300 - 200 * k, operation="SUBTRACT")
        link(nt, r, sub.inputs[0])
        sub.inputs[1].default_value = c
        ab = node(nt, "ShaderNodeMath", -850, 300 - 200 * k, operation="ABSOLUTE")
        link(nt, sub.outputs["Value"], ab.inputs[0])
        lt = node(nt, "ShaderNodeMath", -700, 300 - 200 * k, operation="LESS_THAN")
        link(nt, ab.outputs["Value"], lt.inputs[0])
        lt.inputs[1].default_value = w
        if gap is None:
            gap = lt.outputs["Value"]
        else:
            add = node(nt, "ShaderNodeMath", -550, 300 - 200 * k, operation="ADD")
            link(nt, gap, add.inputs[0])
            link(nt, lt.outputs["Value"], add.inputs[1])
            gap = add.outputs["Value"]

    rough = node(nt, "ShaderNodeMapRange", 600, 200)
    rough.inputs["To Min"].default_value = 0.32
    rough.inputs["To Max"].default_value = 0.1
    link(nt, gap, rough.inputs["Value"])
    link(nt, rough.outputs["Result"], bsdf.inputs["Roughness"])

    ang = node(nt, "ShaderNodeMath", -1000, -300, operation="MULTIPLY")
    link(nt, r, ang.inputs[0])
    ang.inputs[1].default_value = 2 * math.pi * GROOVES
    wave = node(nt, "ShaderNodeMath", -850, -300, operation="SINE")
    link(nt, ang.outputs["Value"], wave.inputs[0])
    lift = node(nt, "ShaderNodeMath", -700, -300, operation="MULTIPLY_ADD")
    link(nt, wave.outputs["Value"], lift.inputs[0])
    lift.inputs[1].default_value = 0.5
    lift.inputs[2].default_value = 0.5
    keep = node(nt, "ShaderNodeMath", -550, -100, operation="SUBTRACT")
    keep.inputs[0].default_value = 1.0
    link(nt, gap, keep.inputs[1])
    height = node(nt, "ShaderNodeMath", -400, -300, operation="MULTIPLY")
    link(nt, lift.outputs["Value"], height.inputs[0])
    link(nt, keep.outputs["Value"], height.inputs[1])
    bump = node(nt, "ShaderNodeBump", 700, -100)
    bump.inputs["Distance"].default_value = GROOVE_DEPTH
    link(nt, height.outputs["Value"], bump.inputs["Height"])
    link(nt, bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def turned_metal(name, color, rough, aniso, freq):
    """Metal with a spun finish: fine concentric lines and a circular anisotropy, so it catches the same wedges of
    light as the grooves around it."""
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (*color, 1)
    bsdf.inputs["Metallic"].default_value = 1.0
    bsdf.inputs["Roughness"].default_value = rough
    set_input(bsdf, ("Anisotropic",), aniso)
    tan = node(nt, "ShaderNodeTangent", -200, -300, direction_type="RADIAL", axis="Z")
    link(nt, tan.outputs["Tangent"], bsdf.inputs["Tangent"])
    tc = node(nt, "ShaderNodeTexCoord", -900, -100)
    length = node(nt, "ShaderNodeVectorMath", -700, -100, operation="LENGTH")
    link(nt, tc.outputs["Object"], length.inputs[0])
    ang = node(nt, "ShaderNodeMath", -500, -100, operation="MULTIPLY")
    link(nt, length.outputs["Value"], ang.inputs[0])
    ang.inputs[1].default_value = 2 * math.pi * freq
    wave = node(nt, "ShaderNodeMath", -350, -100, operation="SINE")
    link(nt, ang.outputs["Value"], wave.inputs[0])
    bump = node(nt, "ShaderNodeBump", -150, -100)
    bump.inputs["Distance"].default_value = 0.0004
    link(nt, wave.outputs["Value"], bump.inputs["Height"])
    link(nt, bump.outputs["Normal"], bsdf.inputs["Normal"])
    return m


def add_mesh(name, verts, faces, mat=None, solid=0.0, bevel=0.0, segments=3):
    me = bpy.data.meshes.new(name)
    me.from_pydata(verts, [], faces)
    me.update()
    ob = bpy.data.objects.new(name, me)
    scene.collection.objects.link(ob)
    if solid:
        s = ob.modifiers.new("solid", "SOLIDIFY")
        s.thickness = solid
        s.offset = -1
    if bevel:
        b = ob.modifiers.new("bevel", "BEVEL")
        b.width = bevel
        b.segments = segments
        b.limit_method = "ANGLE"
    if mat:
        me.materials.append(mat)
    # Flat: smooth shading would blend the top face's normal toward its sides and give the flat parts a false gradient
    for p in me.polygons:
        p.use_smooth = False
    return ob


def circle(name, r, z, mat, solid, bevel=0.0, seg=256):
    v = [(math.cos(2 * math.pi * k / seg) * r, math.sin(2 * math.pi * k / seg) * r, z) for k in range(seg)]
    return add_mesh(name, v, [list(range(seg))], mat, solid, bevel)


def annulus(name, r_in, r_out, z, mat, solid, seg=256):
    v, f = [], []
    for k in range(seg):
        t = 2 * math.pi * k / seg
        v += [(math.cos(t) * r_in, math.sin(t) * r_in, z), (math.cos(t) * r_out, math.sin(t) * r_out, z)]
    for k in range(seg):
        a, b = 2 * k, 2 * ((k + 1) % seg)
        f.append([a, b, b + 1, a + 1])
    return add_mesh(name, v, f, mat, solid)


def capsule(x0, y0, x1, y1, w, steps=16):
    """A bar with round ends, as a convex polygon (the X's strokes)."""
    a = math.atan2(y1 - y0, x1 - x0)
    r = w / 2
    pts = []
    for (cx, cy, start) in ((x1, y1, a - math.pi / 2), (x0, y0, a + math.pi / 2)):
        for k in range(steps + 1):
            t = start + math.pi * k / steps
            pts.append((cx + r * math.cos(t), cy + r * math.sin(t)))
    return pts


# --- the record: the disc, the label, its ring, the X and the spindle hole -----------------------------------------
circle("disco", 1.0, 0.0, vinyl_material(), DISC_THICK, bevel=0.012)

paper = turned_metal("etiqueta", (1.0, 0.15, 0.004), 0.32, 0.7, 260)
circle("etiqueta", LABEL, 0.012, paper, 0.01, bevel=0.004)
ring = turned_metal("aro-etiqueta", (0.55, 0.1, 0.006), 0.22, 0.0, 260)
annulus("aro-etiqueta", RING_R - RING_W / 2, RING_R + RING_W / 2, 0.016, ring, 0.004)

# The X: dark polished metal
ink, ib = principled("tinta", (0.035, 0.035, 0.04), 0.26, 1.0)
# Same height and no bevel, so the two strokes read as one printed X (a bevel would draw a ridge where they cross)
for k, sx in enumerate((1, -1)):
    pts = capsule(-X_HALF, -sx * X_HALF, X_HALF, sx * X_HALF, X_WIDTH)
    # A hair apart in height, so the shared centre doesn't z-fight
    add_mesh(f"x{k}", [(x, y, 0.03 + 0.0006 * k) for x, y in pts], [list(range(len(pts)))], ink, solid=0.012)
circle("agujero", HOLE, 0.04, ink, 0.01)

# --- light and camera -------------------------------------------------------------------------------------------
world = bpy.data.worlds.new("mundo")
scene.world = world
world.use_nodes = True
wn = world.node_tree
env = wn.nodes.new("ShaderNodeTexEnvironment")
env.image = bpy.data.images.load(os.path.join(os.path.dirname(bpy.app.binary_path), f"{bpy.app.version[0]}.{bpy.app.version[1]}",
                                              "datafiles", "studiolights", "world", "studio.exr"))
mp = wn.nodes.new("ShaderNodeMapping")
mp.inputs["Rotation"].default_value = (0, 0, math.radians(135))
tcw = wn.nodes.new("ShaderNodeTexCoord")
wn.links.new(tcw.outputs["Generated"], mp.inputs["Vector"])
wn.links.new(mp.outputs["Vector"], env.inputs["Vector"])
wn.links.new(env.outputs["Color"], wn.nodes["Background"].inputs["Color"])
wn.nodes["Background"].inputs["Strength"].default_value = 0.5


def area(name, loc, energy, size, color=(1, 1, 1)):
    ld = bpy.data.lights.new(name, "AREA")
    ld.energy = energy
    ld.size = size
    ld.color = color
    ob = bpy.data.objects.new(name, ld)
    scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = (Vector((0, 0, 0)) - Vector(loc)).to_track_quat("-Z", "Y").to_euler()


# The strong pair on one diagonal draws the main wedges; the weaker pair on the other closes the X
area("noreste", (2.2, 2.2, 0.8), 300, 1.0, (1.0, 0.95, 0.88))
area("sudoeste", (-2.2, -2.2, 0.8), 220, 1.0, (0.85, 0.9, 1.0))
area("noroeste", (-2.2, 2.2, 1.0), 90, 1.0)
area("sureste", (2.2, -2.2, 1.0), 90, 1.0)
area("arriba", (0.3, 0.8, 3.0), 18, 3.0, (1.0, 0.96, 0.9))

cam_d = bpy.data.cameras.new("cam")
cam_d.type = "ORTHO"
cam_d.ortho_scale = 2.02
cam = bpy.data.objects.new("cam", cam_d)
scene.collection.objects.link(cam)
cam.location = (0, 0, 8)
scene.camera = cam

# --- render -----------------------------------------------------------------------------------------------------
scene.render.engine = "CYCLES"
prefs = bpy.context.preferences.addons["cycles"].preferences
try:
    prefs.compute_device_type = "HIP"
    prefs.get_devices()
    for d in prefs.devices:
        d.use = True
    scene.cycles.device = "GPU"
except Exception:
    scene.cycles.device = "CPU"
scene.cycles.samples = 192
scene.cycles.use_denoising = True
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = SIZE
scene.render.image_settings.file_format = "PNG"
scene.render.image_settings.color_mode = "RGBA"
scene.view_settings.view_transform = "AgX"
try:
    scene.view_settings.look = "AgX - Medium High Contrast"
except Exception:
    pass
scene.render.filepath = OUT
bpy.ops.render.render(write_still=True)
print("OK", OUT)
