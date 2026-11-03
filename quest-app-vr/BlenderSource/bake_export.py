"""
Bakes the cinema lightmap and exports the FBX for Unity.
  Blender -b theater.blend --python bake_export.py -- [size samples]

Two Cycles diffuse bakes (direct + indirect, no albedo) into one shared atlas on
the "Lightmap" UV (Unity UV2):
  A  house lighting: coves, sconces, downlights, step lights, exit signs (screen off)
  B  screen lighting: only the 16x9 screen emitting white radiance 1.0
Stored as T_Lightmap.png:  rgb = sqrt(A / rangeA),  a = sqrt(lum(B) / rangeB)
Unity: light = A + B * screenColour (see TheaterBaked.shader), so the film's colour
spills onto the room with real occlusion and bounce.
"""
import bpy, os, sys, json
import numpy as np

PROJ = os.path.expanduser("~/MoviePlayerVR")
SRC = PROJ + "/BlenderSource"
OUT = PROJ + "/Assets/Theater/Resources/TheaterModel"
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SIZE = int(argv[0]) if len(argv) > 0 else 2048
SAMPLES = int(argv[1]) if len(argv) > 1 else 256

scene = bpy.context.scene
scene.render.engine = 'CYCLES'
prefs = bpy.context.preferences.addons['cycles'].preferences
prefs.compute_device_type = 'METAL'; prefs.get_devices()
for d in prefs.devices: d.use = True
scene.cycles.device = 'GPU'
scene.cycles.samples = SAMPLES
scene.cycles.max_bounces = 6
scene.cycles.diffuse_bounces = 4
scene.render.bake.margin = 6
scene.render.bake.margin_type = 'EXTEND'
if scene.world is None: scene.world = bpy.data.worlds.new("Black")
scene.world.use_nodes = True
scene.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0, 0, 0, 1)

export_coll = bpy.data.collections["Export"]
bake_coll = bpy.data.collections["BakeOnly"]
targets = [o for o in export_coll.objects if o.type == 'MESH' and not o.name.startswith("Theater_Emissive")]

lm = bpy.data.images.new("LM_bake", SIZE, SIZE, alpha=True, float_buffer=True)
lm.colorspace_settings.name = 'Non-Color'

# image node (bake target) in every target material, set active
for m in {s.material for o in targets for s in o.material_slots if s.material}:
    N = m.node_tree.nodes
    node = N.get("LM_target") or N.new("ShaderNodeTexImage")
    node.name = "LM_target"; node.image = lm
    for n in N: n.select = False
    node.select = True; N.active = node

for o in targets:
    o.data.uv_layers.active = o.data.uv_layers["Lightmap"]

emissive = bpy.data.materials["M_Emissive"].node_tree.nodes["Emit"].inputs["Strength"]
cove = bpy.data.materials["M_CoveLED"].node_tree.nodes["Emit"].inputs["Strength"]
screen = bpy.data.objects["BakeScreen"]
screen.data.materials[0].node_tree.nodes["Emit"].inputs["Strength"].default_value = 1.0
E0, C0 = emissive.default_value, cove.default_value

def set_mode(house):
    emissive.default_value = E0 if house else 0.0
    cove.default_value = C0 if house else 0.0
    for o in bake_coll.objects:
        if o.type == 'LIGHT': o.hide_render = not house
    screen.hide_render = house

def bake():
    bpy.ops.object.select_all(action='DESELECT')
    for o in targets: o.select_set(True)
    bpy.context.view_layer.objects.active = targets[0]
    bpy.ops.object.bake(type='DIFFUSE', pass_filter={'DIRECT', 'INDIRECT'}, use_clear=True, margin=6)
    px = np.empty(SIZE * SIZE * 4, np.float32); lm.pixels.foreach_get(px)
    return px.reshape(SIZE, SIZE, 4)

def denoise(img, mask):
    """Light masked gaussian (sigma ~1 px) to knock down residual sampling noise."""
    k = np.array([1, 4, 6, 4, 1], np.float32); k /= k.sum()
    def blur(a):
        a = sum(w * np.roll(a, i - 2, axis=0) for i, w in enumerate(k))
        return sum(w * np.roll(a, i - 2, axis=1) for i, w in enumerate(k))
    m = mask.astype(np.float32)
    num = blur(img * m[..., None]); den = blur(m)[..., None]
    return np.where(den > 1e-4, num / np.maximum(den, 1e-4), img)

set_mode(True)
A = bake()
mask = A[..., 3] > 0.5
set_mode(False)
B = bake()
set_mode(True)

A = denoise(A[..., :3], mask)
B = denoise(B[..., :3], mask)
Bl = B @ np.array([0.2126, 0.7152, 0.0722], np.float32)
rangeA = float(max(np.percentile(A[mask].max(axis=1), 99.7), 1e-3))
rangeB = float(max(np.percentile(Bl[mask], 99.7), 1e-3))
enc = np.zeros((SIZE, SIZE, 4), np.float32)
enc[..., :3] = np.sqrt(np.clip(A / rangeA, 0, 1))
enc[..., 3] = np.sqrt(np.clip(Bl / rangeB, 0, 1))
enc[~mask] = 0

out = bpy.data.images.new("T_Lightmap", SIZE, SIZE, alpha=True)
out.colorspace_settings.name = 'Non-Color'
out.alpha_mode = 'STRAIGHT'
out.pixels.foreach_set(enc.ravel())
out.filepath_raw = OUT + "/T_Lightmap.png"; out.file_format = 'PNG'
scene.render.image_settings.color_mode = 'RGBA'
out.save()
out.filepath = OUT + "/T_Lightmap.png"
bpy.data.images.remove(lm)
for m in bpy.data.materials:
    if m.node_tree and m.node_tree.nodes.get("LM_target"):
        m.node_tree.nodes["LM_target"].image = out

info = {"rangeHouse": rangeA, "rangeScreen": rangeB, "lightmapSize": SIZE, "samples": SAMPLES,
        "screenBakeRadiance": 1.0, "screenBakeSize": [16.0, 9.0]}
with open(OUT + "/TheaterBake.json", "w") as f: json.dump(info, f, indent=1)
print("[bake]", info)

for o in targets:
    o.data.uv_layers.active = o.data.uv_layers["UVMap"]

# ── FBX export: meshes + seat anchor empties only ──
bpy.ops.object.select_all(action='DESELECT')
for o in export_coll.objects: o.select_set(True)
bpy.context.view_layer.objects.active = targets[0]
bpy.ops.export_scene.fbx(filepath=OUT + "/Theater.fbx", use_selection=True,
                         object_types={'MESH', 'EMPTY'}, use_mesh_modifiers=True,
                         apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                         bake_space_transform=True, mesh_smooth_type='FACE', use_tspace=False,
                         colors_type='SRGB', add_leaf_bones=False, bake_anim=False,
                         path_mode='STRIP', embed_textures=False)
print("[bake] exported", OUT + "/Theater.fbx")
bpy.ops.wm.save_as_mainfile(filepath=SRC + "/theater.blend", compress=True)
