"""
Renders a still from the viewer's seat (seat 0, head at the origin looking at the screen).
  Blender -b theater.blend --python render_preview.py -- [width height samples out.png]
The bake-only screen emitter shows a generated "movie frame" for the preview.
"""
import bpy, os, sys, math
import numpy as np

SRC = os.path.expanduser("~/MoviePlayerVR/BlenderSource")
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
W = int(argv[0]) if len(argv) > 0 else 1280
H = int(argv[1]) if len(argv) > 1 else 720
SAMPLES = int(argv[2]) if len(argv) > 2 else 96
OUTFILE = argv[3] if len(argv) > 3 else SRC + "/preview.png"
CAM = argv[4] if len(argv) > 4 else "front"

scene = bpy.context.scene

# ── a sunset "movie frame" for the screen ──
fw, fh = 960, 540
y = np.linspace(0, 1, fh)[:, None]; x = np.linspace(0, 1, fw)[None, :]
ones = np.ones((fh, fw))
sky = np.stack([(0.95 - 0.55 * y) * ones, (0.45 - 0.25 * y) * ones, (0.25 + 0.45 * y) * ones], -1)
sun = np.exp(-(((x - 0.62) * 1.78) ** 2 + (y - 0.33) ** 2) / 0.004)[..., None]
sky = sky + sun * np.array([1.0, 0.85, 0.5])
ridge = 0.22 + 0.06 * np.sin(x * 9.0) + 0.03 * np.sin(x * 23.0 + 1.0)
sky = np.where((y < ridge)[..., None], np.array([0.05, 0.03, 0.06]), sky)
img_px = np.concatenate([np.clip(sky, 0, 1), np.ones((fh, fw, 1))], -1).astype(np.float32)
img = bpy.data.images.new("preview_frame", fw, fh, alpha=False, float_buffer=True)
img.pixels.foreach_set(img_px.ravel())

scr = bpy.data.objects["BakeScreen"]
m = scr.data.materials[0]; nt = m.node_tree
tex = nt.nodes.new("ShaderNodeTexImage"); tex.image = img
nt.links.new(tex.outputs["Color"], nt.nodes["Emit"].inputs["Color"])
nt.nodes["Emit"].inputs["Strength"].default_value = 1.0
scr.hide_render = False; scr.visible_camera = True

# ── camera at the viewer's eye ──
cd = bpy.data.cameras.new("ViewerCam")
cd.sensor_fit = 'HORIZONTAL'; cd.angle = math.radians(92)
cam = bpy.data.objects.new("ViewerCam", cd); scene.collection.objects.link(cam)
if CAM == "front":
    cam.location = (0, 0, 0); cam.rotation_euler = (math.radians(86), 0, math.radians(180))
elif CAM == "back":
    cam.location = (0, 0, 0); cam.rotation_euler = (math.radians(95), 0, 0)
else:  # overview from the back corner
    cam.location = (-7.5, 5.0, 4.0); cam.rotation_euler = (math.radians(78), 0, math.radians(-150))
scene.camera = cam

r = scene.render
r.engine = 'CYCLES'; r.resolution_x = W; r.resolution_y = H; r.resolution_percentage = 100
prefs = bpy.context.preferences.addons['cycles'].preferences
prefs.compute_device_type = 'METAL'; prefs.get_devices()
for d in prefs.devices: d.use = True
scene.cycles.device = 'GPU'
scene.cycles.samples = SAMPLES
scene.cycles.use_denoising = True
scene.cycles.max_bounces = 6
scene.view_settings.view_transform = 'Standard'
scene.view_settings.look = 'None'
scene.view_settings.exposure = 0.0
scene.world = bpy.data.worlds.new("Black") if scene.world is None else scene.world
scene.world.use_nodes = True
scene.world.node_tree.nodes["Background"].inputs["Color"].default_value = (0, 0, 0, 1)
r.image_settings.file_format = 'PNG'
r.filepath = OUTFILE
bpy.ops.render.render(write_still=True)
print("[preview] wrote", OUTFILE)
