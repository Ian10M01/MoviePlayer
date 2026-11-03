"""
Procedural Bigscreen-style cinema for MoviePlayerVR (Quest 2).

Run headless:
  Blender -b --factory-startup --python build_theater.py

Builds the geometry, generates tileable PBR albedo textures (numpy), sets up
Cycles materials + practical lights + a bake-only screen emitter, creates a
shared lightmap UV atlas ("Lightmap", UV channel 2) and saves theater.blend.
Baking and FBX export happen in bake_export.py; the preview in render_preview.py.

COORDINATES
  Blender is Z-up, right handed. The FBX export (axis_forward=-Z, axis_up=Y,
  bake_space_transform) maps Blender (x, y, z) -> Unity (-x, z, -y).
  So in Blender: the viewer's eye is at the origin looking toward -Y, the
  screen plane is y = -12 (Unity z = 12), up is +Z (Unity y), and the floor
  under the viewer is z = -1.2. The room is left/right symmetric.
"""
import bpy, bmesh, math, os, json
import numpy as np
from mathutils import Vector, Matrix

PROJ = os.path.expanduser("~/MoviePlayerVR")
SRC = PROJ + "/BlenderSource"
OUT = PROJ + "/Assets/Theater/Resources/TheaterModel"
os.makedirs(OUT, exist_ok=True)

# ── Layout (metres, Blender space) ─────────────────────────────────────────
EYE = 1.2              # seated eye height above the seat's floor
FLOOR0 = -1.2          # floor under the viewer's seat
SCREEN_Y = -12.0       # Unity ScreenPlaneZ = 12
MASK_Y = -12.05        # black velvet masking just behind the screen plane
FRONT_WALL_Y = -12.4
STAGE_TOP = -1.5       # <= Unity StageTopY (-0.6)
STAGE_FRONT_Y = -10.4
HALF_W = 10.5          # side walls
CEIL = 10.0
BACK_Y = 6.0           # back wall (Unity z = -6)
ROW_PITCH = 1.15
STEP = 0.38            # stadium riser height per row
ARC_R = 26.0           # rows curve gently around the screen
ROWS = list(range(-4, 4))         # 4 rows in front of the viewer, viewer row 0, 3 behind
CENTRE_X = [round(i * 0.6, 3) for i in range(-5, 6)]   # 11 seats, x = -3..3
SIDE_X = [4.8, 5.4, 6.0, 6.6]                          # 4 seats per side block
AISLES = [3.9, 8.8]                                    # aisle centres (both sides)
FLOOR_LOW = FLOOR0 + ROWS[0] * STEP                    # front floor (-2.72)

def arc(x): return x * x / (2.0 * ARC_R)
def row_y(r, x=0.0): return r * ROW_PITCH + arc(x)
def tier_z(r): return FLOOR0 + r * STEP
def tier_front(r, x): return r * ROW_PITCH - 0.6 + arc(x)
def tier_back(r, x): return BACK_Y if r == ROWS[-1] else tier_front(r + 1, x)

# ── Scene reset ────────────────────────────────────────────────────────────
bpy.ops.wm.read_factory_settings(use_empty=True)
scene = bpy.context.scene
scene.unit_settings.system = 'METRIC'

def collection(name):
    c = bpy.data.collections.new(name)
    scene.collection.children.link(c)
    return c

C_EXPORT = collection("Export")
C_BAKE = collection("BakeOnly")      # lights + emitters that are not exported

# ══════════════════════════════════════════════════════════════════════════
#  Procedural textures (numpy, tileable)
# ══════════════════════════════════════════════════════════════════════════
rng = np.random.default_rng(1234)

def value_noise(w, h, px, py):
    lat = rng.random((py, px)).astype(np.float32)
    def axis(n, p):
        t = (np.arange(n, dtype=np.float32) + 0.5) / n * p
        i0 = np.floor(t).astype(np.int32)
        f = t - i0
        f = f * f * (3 - 2 * f)
        return i0 % p, (i0 + 1) % p, f
    x0, x1, fx = axis(w, px)
    y0, y1, fy = axis(h, py)
    a = lat[np.ix_(y0, x0)]; b = lat[np.ix_(y0, x1)]
    c = lat[np.ix_(y1, x0)]; d = lat[np.ix_(y1, x1)]
    fx = fx[None, :]; fy = fy[:, None]
    return (a * (1 - fx) + b * fx) * (1 - fy) + (c * (1 - fx) + d * fx) * fy

def fbm(w, h, px, py, octaves=4):
    out = np.zeros((h, w), np.float32); amp = 1.0; tot = 0.0
    for k in range(octaves):
        out += amp * value_noise(w, h, px << k, py << k); tot += amp; amp *= 0.5
    return out / tot

def grain(w, h, amount):
    return 1.0 + (rng.random((h, w), dtype=np.float32) - 0.5) * 2 * amount

def weave(n, cell):
    x = np.arange(n)
    fx = ((x % cell) + 0.5) / cell
    I, J = np.meshgrid(x // cell, x // cell)
    FX, FY = np.meshgrid(fx, fx)
    over = ((I + J) % 2) == 0
    across = np.where(over, FX, FY); along = np.where(over, FY, FX)
    return (np.sqrt(np.sin(np.pi * across)) * (0.7 + 0.3 * np.sin(np.pi * along))).astype(np.float32)

def save_png(name, rgb):
    h, w, _ = rgb.shape
    rgba = np.concatenate([np.clip(rgb, 0, 1), np.ones((h, w, 1), np.float32)], axis=2)
    img = bpy.data.images.new(name, w, h, alpha=False)
    img.pixels.foreach_set(rgba.astype(np.float32).ravel())
    path = f"{OUT}/{name}.png"
    img.filepath_raw = path; img.file_format = 'PNG'; img.save()
    img.filepath = path
    return img

def tint(v, col):
    return v[..., None] * np.array(col, np.float32)[None, None, :]

def make_textures():
    T = {}
    # Seat / curtain velvet (deep cinema red): fine weave + crushed-pile mottling
    n = 512
    wv = weave(n, 8)
    v = (0.72 + 0.28 * wv) * (0.78 + 0.32 * fbm(n, n, 6, 6)) * grain(n, n, 0.06)
    T["SeatFabric"] = save_png("T_SeatFabric", tint(v, (0.50, 0.045, 0.06)))
    # Acoustic wall fabric: coarse heathered charcoal
    wv = weave(n, 16)
    v = (0.80 + 0.20 * wv) * (0.88 + 0.18 * fbm(n, n, 8, 8)) * grain(n, n, 0.12)
    T["WallFabric"] = save_png("T_WallFabric", tint(v, (0.19, 0.175, 0.17)))
    # Black velvet (masking, stage, front wall)
    v = (0.75 + 0.35 * fbm(256, 256, 4, 4)) * grain(256, 256, 0.10)
    T["Velvet"] = save_png("T_Velvet", tint(v, (0.030, 0.027, 0.030)))
    # Cinema carpet: dark plum with gold diamond lattice, teal medallions
    n = 1024; cell = 256
    x = (np.arange(n) % cell + 0.5) / (cell / 2) - 1
    U, V = np.meshgrid(x, x)
    ax, ay = np.abs(U), np.abs(V)
    d = ax + ay
    aa = 2.5 / (cell / 2)
    def band(dist, half): return 1 - np.clip((np.abs(dist) - half) / aa, 0, 1)
    def disc(dist, r): return 1 - np.clip((dist - r) / aa, 0, 1)
    base = np.array((0.11, 0.035, 0.065), np.float32)
    gold = np.array((0.58, 0.42, 0.17), np.float32)
    red = np.array((0.45, 0.07, 0.10), np.float32)
    teal = np.array((0.05, 0.22, 0.25), np.float32)
    col = np.broadcast_to(base, (n, n, 3)).copy()
    def lay(mask, c):
        nonlocal col
        col = col * (1 - mask[..., None]) + c[None, None, :] * mask[..., None]
    rc = np.sqrt((1 - ax) ** 2 + (1 - ay) ** 2)
    lay(disc(rc, 0.30), teal)
    lay(band(rc - 0.30, 0.025), gold)
    lay(band(d - 0.80, 0.035), gold)
    lay(disc(d, 0.36), red)
    lay(band(d - 0.36, 0.02), gold)
    lay(disc(d, 0.10), teal)
    md = np.minimum(np.sqrt(ax ** 2 + (1 - ay) ** 2), np.sqrt((1 - ax) ** 2 + ay ** 2))
    lay(disc(md, 0.06), gold)
    pile = (0.86 + 0.22 * fbm(n, n, 16, 16)) * grain(n, n, 0.14)
    T["Carpet"] = save_png("T_Carpet", col * pile[..., None])
    # Dark walnut (grain along V)
    w, h = 512, 512
    warp = fbm(w, h, 4, 2, 4)
    uu = (np.arange(w) + 0.5) / w
    rings = 0.5 + 0.5 * np.sin(2 * np.pi * (uu[None, :] * 9 + warp * 2.2))
    t = rings ** 3 * 0.65 + fbm(w, h, 32, 4, 3) * 0.35
    pores = np.where(rng.random((h // 4, w)) < 0.05, 0.75, 1.0).repeat(4, axis=0)
    light = np.array((0.30, 0.165, 0.085), np.float32); dark = np.array((0.11, 0.055, 0.03), np.float32)
    wood = light[None, None, :] * (1 - t[..., None]) + dark[None, None, :] * t[..., None]
    T["Wood"] = save_png("T_Wood", wood * (pores * grain(w, h, 0.04))[..., None])
    # Armrest leatherette: dark, pebbled grain
    n = 256
    pebble = fbm(n, n, 32, 32, 2)
    v = (0.80 + 0.35 * pebble) * grain(n, n, 0.04)
    T["Leather"] = save_png("T_Leather", tint(v, (0.075, 0.062, 0.055)))
    # Brushed dark metal (streaks along U)
    v = (0.82 + 0.25 * fbm(n, n, 2, 64, 3)) * grain(n, n, 0.05)
    T["Metal"] = save_png("T_Metal", tint(v, (0.30, 0.30, 0.31)))
    # Seat-back plastic shell: charcoal with fine speckle
    v = (0.92 + 0.10 * fbm(n, n, 8, 8, 3)) * grain(n, n, 0.05)
    T["Plastic"] = save_png("T_Plastic", tint(v, (0.065, 0.063, 0.068)))
    # Ceiling: 0.6 m acoustic tiles (texture covers 1.2 m = 2x2 tiles)
    n = 512
    p = np.arange(n) % 256
    seam = ((p < 3) | (p > 252)).astype(np.float32)
    S = np.maximum(seam[None, :], seam[:, None])
    v = (0.85 + 0.25 * fbm(n, n, 16, 16, 3)) * grain(n, n, 0.15) * (1 - 0.55 * S)
    T["Ceiling"] = save_png("T_Ceiling", tint(v, (0.085, 0.085, 0.09)))
    return T

TEX = make_textures()

# Texture tile size in metres (UV0 is in metres; Unity uses the same tiling)
TILE = {"SeatFabric": 0.30, "WallFabric": 1.0, "Velvet": 1.0, "Carpet": 2.0, "Wood": 1.0,
        "Leather": 0.30, "Metal": 0.5, "Plastic": 0.5, "Ceiling": 1.2}
ROUGH = {"SeatFabric": 0.95, "WallFabric": 0.95, "Velvet": 1.0, "Carpet": 1.0, "Wood": 0.55,
         "Leather": 0.6, "Metal": 0.35, "Plastic": 0.5, "Ceiling": 0.95}

# ══════════════════════════════════════════════════════════════════════════
#  Materials
# ══════════════════════════════════════════════════════════════════════════
def make_material(key):
    m = bpy.data.materials.new("M_" + key)
    m.use_nodes = True
    nt = m.node_tree; N = nt.nodes; L = nt.links
    bsdf = N.get("Principled BSDF")
    uv = N.new("ShaderNodeUVMap"); uv.uv_map = "UVMap"
    mp = N.new("ShaderNodeMapping"); s = 1.0 / TILE[key]
    mp.inputs["Scale"].default_value = (s, s, 1)
    tx = N.new("ShaderNodeTexImage"); tx.image = TEX[key]
    L.new(uv.outputs["UV"], mp.inputs["Vector"])
    L.new(mp.outputs["Vector"], tx.inputs["Vector"])
    L.new(tx.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = ROUGH[key]
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.25 if key != "Metal" else 0.5
    if key == "Metal":
        bsdf.inputs["Metallic"].default_value = 0.7
    return m

MATS = {k: make_material(k) for k in TILE}

def emission_material(name, use_attr=True, color=(1, 1, 1), strength=1.0):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nt = m.node_tree; N = nt.nodes; L = nt.links
    for n in list(N):
        if n.type != 'OUTPUT_MATERIAL': N.remove(n)
    out = [n for n in N if n.type == 'OUTPUT_MATERIAL'][0]
    em = N.new("ShaderNodeEmission"); em.name = "Emit"
    em.inputs["Color"].default_value = (*color, 1)
    em.inputs["Strength"].default_value = strength
    if use_attr:
        at = N.new("ShaderNodeVertexColor"); at.layer_name = "Col"
        L.new(at.outputs["Color"], em.inputs["Color"])
    L.new(em.outputs["Emission"], out.inputs["Surface"])
    return m

MAT_EMISSIVE = emission_material("M_Emissive", True, strength=2.5)   # exported practicals (vertex colour)
MAT_COVE = emission_material("M_CoveLED", False, (1.0, 0.70, 0.42), 22.0)   # hidden cove emitters (bake only)

# ══════════════════════════════════════════════════════════════════════════
#  Mesh accumulation (numpy, fast instancing)
# ══════════════════════════════════════════════════════════════════════════
class Accum:
    def __init__(self):
        self.co, self.vi, self.ls, self.uv, self.col, self.smooth = [], [], [], [], [], []
        self.nv = 0; self.nl = 0

    def add_mesh(self, me, M=None, color=(1, 1, 1, 1), smooth=False):
        n = len(me.vertices)
        co = np.empty(n * 3, np.float32); me.vertices.foreach_get("co", co); co = co.reshape(-1, 3)
        if M is not None:
            m = np.array(M, np.float32)
            co = co @ m[:3, :3].T + m[:3, 3]
        nl = len(me.loops)
        vi = np.empty(nl, np.int32); me.loops.foreach_get("vertex_index", vi)
        npoly = len(me.polygons)
        ls = np.empty(npoly, np.int32); me.polygons.foreach_get("loop_start", ls)
        uv = np.zeros(nl * 2, np.float32)
        if me.uv_layers: me.uv_layers[0].data.foreach_get("uv", uv)
        sm = np.empty(npoly, bool)
        if smooth is None:
            me.polygons.foreach_get("use_smooth", sm)
        else:
            sm[:] = smooth
        self.co.append(co); self.vi.append(vi + self.nv); self.ls.append(ls + self.nl)
        self.uv.append(uv.reshape(-1, 2))
        self.col.append(np.tile(np.array(color, np.float32), (nl, 1)))
        self.smooth.append(sm)
        self.nv += n; self.nl += nl

    def add_bm(self, bm, M=None, color=(1, 1, 1, 1), smooth=False):
        me = bpy.data.meshes.new("_tmp")
        bm.to_mesh(me)
        self.add_mesh(me, M, color, smooth)
        bpy.data.meshes.remove(me)

    def to_mesh(self, name, with_color=False):
        me = bpy.data.meshes.new(name)
        co = np.concatenate(self.co); vi = np.concatenate(self.vi); ls = np.concatenate(self.ls)
        me.vertices.add(len(co)); me.vertices.foreach_set("co", co.ravel())
        me.loops.add(len(vi)); me.loops.foreach_set("vertex_index", vi)
        me.polygons.add(len(ls)); me.polygons.foreach_set("loop_start", ls)
        me.update(calc_edges=True)
        uvl = me.uv_layers.new(name="UVMap")
        uvl.data.foreach_set("uv", np.concatenate(self.uv).ravel())
        sm = np.concatenate(self.smooth)
        attr = me.attributes.get("sharp_face") or me.attributes.new("sharp_face", 'BOOLEAN', 'FACE')
        attr.data.foreach_set("value", ~sm)
        if with_color:
            ca = me.color_attributes.new("Col", 'BYTE_COLOR', 'CORNER')
            c = np.concatenate(self.col).ravel()
            try: ca.data.foreach_set("color_srgb", c)
            except Exception: ca.data.foreach_set("color", c)
        me.validate(clean_customdata=False)
        me.update()
        return me

    def build(self, name, mat, coll=C_EXPORT, with_color=False):
        if not self.co: return None
        me = self.to_mesh(name, with_color)
        me.materials.append(mat)
        ob = bpy.data.objects.new(name, me)
        coll.objects.link(ob)
        return ob

# ── bmesh helpers ──────────────────────────────────────────────────────────
def box_uv(bm, scale=1.0):
    uvl = bm.loops.layers.uv.verify()
    for f in bm.faces:
        f.normal_update()
        n = f.normal; a = (abs(n.x), abs(n.y), abs(n.z))
        for l in f.loops:
            c = l.vert.co
            if a[0] >= a[1] and a[0] >= a[2]: uv = (c.y, c.z)
            elif a[1] >= a[2]: uv = (c.x, c.z)
            else: uv = (c.x, c.y)
            l[uvl].uv = (uv[0] * scale, uv[1] * scale)

def bm_box(size, center=(0, 0, 0), bevel=0.0, segs=1, cuts_x=0, cuts_z=0):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    for axis, cuts in ((0, cuts_x), (2, cuts_z)):
        if cuts:
            es = [e for e in bm.edges if abs((e.verts[0].co - e.verts[1].co)[axis]) > 1e-6]
            bmesh.ops.subdivide_edges(bm, edges=es, cuts=cuts, use_grid_fill=True)
    if bevel > 0:
        sharp = [e for e in bm.edges if e.calc_face_angle(0) > 0.5]
        bmesh.ops.bevel(bm, geom=sharp, offset=bevel, offset_type='OFFSET', segments=segs,
                        profile=0.5, affect='EDGES', clamp_overlap=True)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    box_uv(bm)
    bmesh.ops.translate(bm, verts=bm.verts, vec=Vector(center))
    return bm

def orient(f, desired):
    f.normal_update()
    if f.normal.dot(desired) < 0: f.normal_flip()

def bm_grid(P, nu, nv, facing, uvf=None, closed_u=False):
    """Parametric surface. facing(center)->desired normal. uvf(co, normal)->uv (default box)."""
    bm = bmesh.new()
    cols = nu if closed_u else nu + 1
    V = [[bm.verts.new(P(i / nu, j / nv)) for j in range(nv + 1)] for i in range(cols)]
    for i in range(nu):
        i1 = (i + 1) % cols
        for j in range(nv):
            f = bm.faces.new((V[i][j], V[i1][j], V[i1][j + 1], V[i][j + 1]))
            orient(f, facing(f.calc_center_median()))
    if uvf is None:
        box_uv(bm)
    else:
        uvl = bm.loops.layers.uv.verify()
        for f in bm.faces:
            for l in f.loops: l[uvl].uv = uvf(l.vert.co, f.normal)
    return bm

def bm_quad(center, normal, up, w, h, uv_rect=None):
    """Flat rectangle facing `normal`."""
    n = Vector(normal).normalized(); u = Vector(up).normalized()
    r = u.cross(n).normalized()          # right as seen by a viewer looking at the face
    c = Vector(center)
    pts = [c - r * w / 2 - u * h / 2, c + r * w / 2 - u * h / 2, c + r * w / 2 + u * h / 2, c - r * w / 2 + u * h / 2]
    bm = bmesh.new()
    vs = [bm.verts.new(p) for p in pts]
    f = bm.faces.new(vs)
    uvl = bm.loops.layers.uv.verify()
    if uv_rect is None:
        for l, p in zip(f.loops, pts): l[uvl].uv = (p.dot(r), p.dot(u))
    else:
        u0, v0, u1, v1 = uv_rect
        for l, uv in zip(f.loops, ((u0, v0), (u1, v0), (u1, v1), (u0, v1))): l[uvl].uv = uv
    orient(f, n)
    return bm

def bm_cupholder(segs=12, r_out=0.048, r_in=0.040, top=0.0, height=0.035, depth=0.09):
    bm = bmesh.new()
    def ring(r, z): return [bm.verts.new((r * math.cos(2 * math.pi * i / segs), r * math.sin(2 * math.pi * i / segs), z)) for i in range(segs)]
    ob, ot = ring(r_out, top - height), ring(r_out, top)
    it, ib = ring(r_in, top), ring(r_in, top - depth)
    for i in range(segs):
        j = (i + 1) % segs
        mid = (math.cos(2 * math.pi * (i + .5) / segs), math.sin(2 * math.pi * (i + .5) / segs), 0)
        orient(bm.faces.new((ob[i], ob[j], ot[j], ot[i])), Vector(mid))
        orient(bm.faces.new((ot[i], ot[j], it[j], it[i])), Vector((0, 0, 1)))
        orient(bm.faces.new((it[i], it[j], ib[j], ib[i])), -Vector(mid))
    orient(bm.faces.new(ib), Vector((0, 0, 1)))
    box_uv(bm)
    return bm

def rot_x(deg): return Matrix.Rotation(math.radians(deg), 4, 'X')
def rot_z(rad): return Matrix.Rotation(rad, 4, 'Z')
def trans(x, y, z): return Matrix.Translation((x, y, z))

def transform_bm(bm, M):
    bmesh.ops.transform(bm, matrix=M, verts=bm.verts)

def bend_bm(bm, k):
    """Wrap a backrest around the sitter (who sits toward -Y): y -= k x^2."""
    for v in bm.verts: v.co.y -= k * v.co.x * v.co.x

# ══════════════════════════════════════════════════════════════════════════
#  Seat template (local: origin on the floor below the head, faces -Y)
# ══════════════════════════════════════════════════════════════════════════
def seat_parts(hi):
    """hi=True: detailed seat for the rows around the viewer; False: lighter LOD."""
    sg = 2 if hi else 1
    parts = {"SeatFabric": Accum(), "Plastic": Accum(), "Leather": Accum(), "Metal": Accum()}
    # Seat cushion: front edge raised 4 degrees, crowned top
    bm = bm_box((0.49, 0.50, 0.12), bevel=0.045, segs=sg, cuts_x=1)
    for v in bm.verts:
        if v.co.z > 0: v.co.z += 0.012 * (1 - (v.co.x / 0.245) ** 2)
    transform_bm(bm, trans(0, -0.07, 0.40) @ rot_x(-4))
    parts["SeatFabric"].add_bm(bm, smooth=True); bm.free()
    # Back group (backrest + headrest + shell), tilted back 12 degrees about its base
    pivot = trans(0, 0.23, 0.42) @ rot_x(-12)
    bm = bm_box((0.50, 0.11, 0.62), (0, 0, 0.34), bevel=0.045, segs=sg, cuts_x=3 if hi else 2, cuts_z=1)
    for v in bm.verts:                      # lumbar bulge toward the sitter
        if v.co.y < 0: v.co.y -= 0.025 * math.sin(math.pi * min(max((v.co.z - 0.03) / 0.62, 0), 1))
    bend_bm(bm, 0.45); transform_bm(bm, pivot)
    parts["SeatFabric"].add_bm(bm, smooth=True); bm.free()
    bm = bm_box((0.42, 0.10, 0.17), (0, -0.075, 0.585), bevel=0.045, segs=sg, cuts_x=1)
    bend_bm(bm, 0.45); transform_bm(bm, pivot)
    parts["SeatFabric"].add_bm(bm, smooth=True); bm.free()
    bm = bm_box((0.54, 0.045, 0.70), (0, 0.075, 0.33), bevel=0.02, segs=1, cuts_x=2)
    bend_bm(bm, 0.45); transform_bm(bm, pivot)
    parts["Plastic"].add_bm(bm, smooth=True); bm.free()
    # Seat pan under the cushion
    bm = bm_box((0.47, 0.44, 0.05), (0, -0.04, 0.31))
    parts["Plastic"].add_bm(bm); bm.free()
    return parts

def armrest_parts(hi):
    parts = {"Plastic": Accum(), "Leather": Accum(), "Metal": Accum()}
    bm = bm_box((0.05, 0.50, 0.58), (0, 0.0, 0.29), bevel=0.015 if hi else 0.0, segs=1)   # side standard
    parts["Plastic"].add_bm(bm); bm.free()
    bm = bm_box((0.11, 0.44, 0.02), (0, 0.0, 0.01))                                       # floor foot
    parts["Metal"].add_bm(bm); bm.free()
    bm = bm_box((0.095, 0.58, 0.055), (0, 0.0, 0.625), bevel=0.024, segs=2 if hi else 1)  # padded arm top
    parts["Leather"].add_bm(bm, smooth=True); bm.free()
    bm = bm_cupholder(segs=12 if hi else 8, top=0.665)                                    # cup holder
    transform_bm(bm, trans(0, -0.31, 0))
    parts["Metal"].add_bm(bm, smooth=True); bm.free()
    return parts

def template_mesh(acc, name):
    return acc.to_mesh(name) if acc.co else None

SEAT_T = {hi: {k: template_mesh(a, f"T_seat_{k}_{hi}") for k, a in seat_parts(hi).items()} for hi in (True, False)}
ARM_T = {hi: {k: template_mesh(a, f"T_arm_{k}_{hi}") for k, a in armrest_parts(hi).items()} for hi in (True, False)}
def is_hi(x, r): return abs(r) <= 1 and abs(x) <= 2.5

# ══════════════════════════════════════════════════════════════════════════
#  Seating
# ══════════════════════════════════════════════════════════════════════════
ZONES = ("Front", "Rear")                  # rows in front of the viewer / viewer row and behind
SEAT_ACC = {(k, z): Accum() for k in ("SeatFabric", "Plastic", "Leather", "Metal") for z in ZONES}

def seat_matrix(x, r):
    y = row_y(r, x); z = tier_z(r)
    dx, dy = -x, SCREEN_Y - y
    theta = math.atan2(dx / math.hypot(dx, dy), -dy / math.hypot(dx, dy))
    return trans(x, y, z) @ rot_z(theta), theta

seats = []   # (sort key, x, r, head position)
for r in ROWS:
    zone = "Front" if r < 0 else "Rear"
    for block in (CENTRE_X, [-x for x in reversed(SIDE_X)], SIDE_X):
        for i, x in enumerate(block):
            M, theta = seat_matrix(x, r)
            hi = is_hi(x, r)
            for k, me in SEAT_T[hi].items():
                if me: SEAT_ACC[(k, zone)].add_mesh(me, M, smooth=None)
            arms = [-0.30] + ([0.30] if i == len(block) - 1 else [])
            for ax in arms:
                Ma = M @ trans(ax, 0, 0)
                for k, me in ARM_T[hi].items():
                    if me: SEAT_ACC[(k, zone)].add_mesh(me, Ma, smooth=None)
            head = (x, row_y(r, x), tier_z(r) + EYE)
            key = abs(x) / 0.6 * 1.0 + abs(r) * 2.2 + (0.5 if r > 0 else 0) + (0.01 if x < 0 else 0)
            seats.append((key, x, r, head, theta))

for (k, zone), acc in SEAT_ACC.items():
    acc.build(f"Theater_{k}_Seats{zone}", MATS[k])

seats.sort(key=lambda s: s[0])
assert abs(seats[0][1]) < 1e-6 and seats[0][2] == 0
anchors = []
for i, (_, x, r, head, theta) in enumerate(seats):
    e = bpy.data.objects.new(f"SeatAnchor_{i:03d}", None)
    e.empty_display_size = 0.15
    e.location = head
    e.rotation_euler = (0, 0, theta)
    C_EXPORT.objects.link(e)
    anchors.append({"index": i, "row": r, "unity": [-head[0], head[2], -head[1]]})
e = bpy.data.objects.new("ScreenCenter", None); e.location = (0, SCREEN_Y, 3.9); C_EXPORT.objects.link(e)

# ══════════════════════════════════════════════════════════════════════════
#  Room
# ══════════════════════════════════════════════════════════════════════════
A = {k: Accum() for k in ("Carpet", "WallFabric", "Wood", "Velvet", "Ceiling", "Metal", "SeatFabric")}
EMIS = Accum()
NU = 48

def add(key, bm, smooth=False):
    A[key].add_bm(bm, smooth=smooth); bm.free()

def emis(bm, color):
    EMIS.add_bm(bm, color=(*color, 1)); bm.free()

UP = Vector((0, 0, 1))
# Stadium tiers (carpet), risers, metal nosing
for r in ROWS:
    z = tier_z(r)
    def P(u, v, r=r, z=z):
        x = -HALF_W + 2 * HALF_W * u
        yf = STAGE_FRONT_Y if r == ROWS[0] else tier_front(r, x)
        return Vector((x, yf + (tier_back(r, x) - yf) * v, z))
    add("Carpet", bm_grid(P, NU, 1, lambda c: UP))
    if r != ROWS[0]:
        z0 = tier_z(r - 1)
        def R(u, v, r=r, z0=z0, z=z):
            x = -HALF_W + 2 * HALF_W * u
            return Vector((x, tier_front(r, x), z0 + (z - z0) * v))
        add("Carpet", bm_grid(R, NU, 1, lambda c: Vector((0, -1, 0))))
        # nosing: thin brushed-metal edge strip wrapping the tread edge
        def Nt(u, v, r=r, z=z):
            x = -HALF_W + 2 * HALF_W * u
            return Vector((x, tier_front(r, x) + 0.05 * v, z + 0.004))
        def Nf(u, v, r=r, z=z):
            x = -HALF_W + 2 * HALF_W * u
            return Vector((x, tier_front(r, x) - 0.004, z - 0.035 * v))
        add("Metal", bm_grid(Nt, NU, 1, lambda c: UP))
        add("Metal", bm_grid(Nf, NU, 1, lambda c: Vector((0, -1, 0))))

# Aisle half-steps with step lights (both sides, both aisles)
STEP_LIGHTS = []
AMBER = (1.0, 0.55, 0.20)
for side in (-1, 1):
    for ac in AISLES:
        c = side * ac
        phi = math.atan(c / ARC_R)
        for r in ROWS[1:]:
            z0 = tier_z(r - 1)
            Mloc = trans(c, tier_front(r, c), z0) @ rot_z(phi)     # local: x along riser, -y toward screen
            bm = bm_box((1.2, 0.45, STEP / 2), (0, -0.225, STEP / 4))
            transform_bm(bm, Mloc); add("Carpet", bm)
            # light strip on the half-step face and on the riser face above it
            for (py, pz) in ((-0.452, STEP / 2 - 0.035), (-0.004, STEP - 0.045)):
                q = bm_quad((0, py, pz), (0, -1, 0), (0, 0, 1), 0.7, 0.022)
                transform_bm(q, Mloc); emis(q, AMBER)
                STEP_LIGHTS.append((Mloc @ Vector((0, py - 0.03, pz))))
            # metal nosing on the half step
            bm = bm_box((1.2, 0.04, 0.008), (0, -0.43, STEP / 2 + 0.004))
            transform_bm(bm, Mloc); add("Metal", bm)

# Stage (velvet), front lip LED
add("Velvet", bm_box((2 * HALF_W, MASK_Y - STAGE_FRONT_Y, STAGE_TOP - FLOOR_LOW + 0.2),
                     (0, (MASK_Y + STAGE_FRONT_Y) / 2, (STAGE_TOP + FLOOR_LOW - 0.2) / 2)))
emis(bm_quad((0, STAGE_FRONT_Y - 0.002, FLOOR_LOW + 0.08), (0, 1, 0), UP, 2 * HALF_W - 0.4, 0.02), (0.55, 0.28, 0.10))

# Masking (screen surround) and front wall: black velvet
add("Velvet", bm_quad((0, MASK_Y, (FLOOR_LOW + CEIL) / 2), (0, 1, 0), UP, 2 * HALF_W, CEIL - FLOOR_LOW))
add("Velvet", bm_quad((0, FRONT_WALL_Y, (FLOOR_LOW + CEIL) / 2), (0, 1, 0), UP, 2 * HALF_W, CEIL - FLOOR_LOW))

# Side curtains and valance (pleated red velvet), all outside the screen region
def pleats(x0, x1, z0, z1, yc, amp, pitch):
    count = max(1, round((x1 - x0) / pitch))
    def P(u, v):
        x = x0 + (x1 - x0) * u
        return Vector((x, yc + amp * math.sin(2 * math.pi * (x - x0) / ((x1 - x0) / count)), z0 + (z1 - z0) * v))
    return bm_grid(P, count * 6, 2, lambda c: Vector((0, 1, 0)))
for s in (-1, 1):
    x0, x1 = sorted((s * 8.5, s * HALF_W))
    add("SeatFabric", pleats(x0, x1, STAGE_TOP, CEIL, -11.85, 0.10, 0.28), smooth=True)
add("SeatFabric", pleats(-HALF_W, HALF_W, 8.8, CEIL, -11.72, 0.07, 0.35), smooth=True)

# Walls: velvet backing, acoustic fabric panels, walnut battens and trim
PANEL_T = 0.06
def side_wall(s):
    xw = s * HALF_W; n = Vector((-s, 0, 0))
    y0, y1 = -11.6, BACK_Y
    add("Velvet", bm_quad((xw, (FRONT_WALL_Y + BACK_Y) / 2, (FLOOR_LOW + CEIL) / 2), n, UP, BACK_Y - FRONT_WALL_Y, CEIL - FLOOR_LOW - 0.5))
    count = 11; pw = (y1 - y0) / count
    for i in range(count):
        yc = y0 + pw * (i + 0.5)
        add("WallFabric", bm_box((PANEL_T, pw - 0.05, 11.4), (xw - s * PANEL_T / 2, yc, -3.0 + 5.7), bevel=0.02, segs=1))
    for i in range(count + 1):
        add("Wood", bm_box((0.09, 0.07, 11.4), (xw - s * 0.045, y0 + pw * i, 2.7)))
    add("Wood", bm_box((0.10, y1 - y0, 0.12), (xw - s * 0.05, (y0 + y1) / 2, 8.46)))
    # cove soffit (painted ceiling finish) with wood fascia
    add("Ceiling", bm_box((0.6, y1 - FRONT_WALL_Y, 0.7), (xw - s * 0.3, (y1 + FRONT_WALL_Y) / 2, 8.9)))
    add("Wood", bm_box((0.03, y1 - FRONT_WALL_Y, 0.16), (xw - s * 0.615, (y1 + FRONT_WALL_Y) / 2, 9.2)))
for s in (-1, 1): side_wall(s)

def back_wall():
    yw = BACK_Y; n = Vector((0, -1, 0))
    add("Velvet", bm_quad((0, yw, 5.0), n, UP, 2 * HALF_W, 10.5))
    count = 14; pw = 2 * (HALF_W - 0.6) / count; x0 = -HALF_W + 0.6
    for i in range(count):
        add("WallFabric", bm_box((pw - 0.05, PANEL_T, 8.9), (x0 + pw * (i + .5), yw - PANEL_T / 2, -0.5 + 4.45), bevel=0.02, segs=1))
    for i in range(count + 1):
        add("Wood", bm_box((0.07, 0.09, 8.9), (x0 + pw * i, yw - 0.045, 3.95)))
    add("Wood", bm_box((2 * HALF_W, 0.10, 0.12), (0, yw - 0.05, 8.46)))
    add("Ceiling", bm_box((2 * HALF_W - 1.2, 0.6, 0.7), (0, yw - 0.3, 8.9)))
    add("Wood", bm_box((2 * HALF_W - 1.2, 0.03, 0.16), (0, yw - 0.615, 9.2)))
back_wall()

# Ceiling with recessed downlights
add("Ceiling", bm_quad((0, (FRONT_WALL_Y + BACK_Y) / 2, CEIL), (0, 0, -1), (0, 1, 0), 2 * HALF_W, BACK_Y - FRONT_WALL_Y))
DOWNLIGHTS = []
for x in (-6.0, -2.0, 2.0, 6.0):
    for y in (-7.5, -3.0, 1.5):
        segs = 10
        bm = bmesh.new()
        ring = [bm.verts.new((x + 0.09 * math.cos(2 * math.pi * i / segs), y + 0.09 * math.sin(2 * math.pi * i / segs), CEIL - 0.004)) for i in range(segs)]
        f = bm.faces.new(ring); orient(f, Vector((0, 0, -1))); box_uv(bm)
        emis(bm, (1.0, 0.88, 0.70))
        DOWNLIGHTS.append((x, y))

# Doors, exit signs, sconces
def exit_sign(center, n):
    """Lit EXIT sign on a wall; n = direction the face points (into the room)."""
    n = Vector(n); c = Vector(center)
    right = (-n).cross(UP).normalized()
    M = Matrix((right.to_4d(), UP.to_4d(), n.to_4d(), (0, 0, 0, 1))).transposed()
    M.translation = c
    housing = bm_box((0.44, 0.17, 0.06), (0, 0, -0.03))
    transform_bm(housing, M); add("Metal", housing)
    face = bm_quad((0, 0, 0.002), (0, 0, 1), (0, 1, 0), 0.40, 0.14)
    transform_bm(face, M); emis(face, (0.05, 0.75, 0.25))
    cu = bpy.data.curves.new("exit_txt", "FONT")
    cu.body = "EXIT"; cu.size = 0.105; cu.align_x = 'CENTER'; cu.align_y = 'CENTER'
    ob = bpy.data.objects.new("exit_txt", cu); scene.collection.objects.link(ob)
    me = bpy.data.meshes.new_from_object(ob.evaluated_get(bpy.context.evaluated_depsgraph_get()))
    bm = bmesh.new(); bm.from_mesh(me)
    for f in bm.faces: orient(f, Vector((0, 0, 1)))
    bmesh.ops.translate(bm, verts=bm.verts, vec=(0, 0, 0.004))
    box_uv(bm); transform_bm(bm, M); emis(bm, (1, 1, 1))
    bpy.data.objects.remove(ob); bpy.data.curves.remove(cu); bpy.data.meshes.remove(me)
    return c + n * 0.25

def door(center_floor, n, w=1.2, h=2.2):
    n = Vector(n); c = Vector(center_floor)
    right = (-n).cross(UP).normalized()
    M = Matrix((right.to_4d(), UP.to_4d(), n.to_4d(), (0, 0, 0, 1))).transposed(); M.translation = c
    frame = bm_box((w + 0.16, h + 0.08, 0.10), (0, (h + 0.08) / 2, 0.05)); transform_bm(frame, M); add("Metal", frame)
    leaf = bm_box((w, h, 0.12), (0, h / 2, 0.06)); transform_bm(leaf, M); add("Wood", leaf)
    bar = bm_box((w * 0.8, 0.05, 0.05), (0, 1.0, 0.15)); transform_bm(bar, M); add("Metal", bar)

EXIT_LIGHTS = []
for s in (-1, 1):
    xw = s * (HALF_W - PANEL_T)
    door((xw, -9.6, FLOOR_LOW), (-s, 0, 0))
    EXIT_LIGHTS.append(exit_sign((xw, -9.6, FLOOR_LOW + 2.62), (-s, 0, 0)))
    door((s * 9.0, BACK_Y - PANEL_T, tier_z(ROWS[-1])), (0, -1, 0))
    EXIT_LIGHTS.append(exit_sign((s * 9.0, BACK_Y - PANEL_T, tier_z(ROWS[-1]) + 2.62), (0, -1, 0)))

SCONCES = []
for s in (-1, 1):
    xw = s * (HALF_W - PANEL_T)
    for y in (-6.0, -1.5, 3.0):
        z = 3.6
        add("Wood", bm_box((0.12, 0.22, 0.46), (xw - s * 0.06, y, z), bevel=0.01, segs=1))
        for dz in (0.232, -0.232):
            q = bm_quad((xw - s * 0.06, y, z + dz), (0, 0, 1 if dz > 0 else -1), (1, 0, 0), 0.20, 0.10)
            emis(q, (1.0, 0.80, 0.55))
        SCONCES.append((xw - s * 0.16, y, z))

# Projection booth ports
BOOTH = []
for x in (-0.8, 0.8):
    emis(bm_quad((x, BACK_Y - PANEL_T - 0.005, 6.2), (0, -1, 0), UP, 0.55, 0.32), (0.80, 0.88, 1.0))
    add("Metal", bm_box((0.65, 0.04, 0.42), (x, BACK_Y - PANEL_T - 0.0, 6.2)))
    BOOTH.append((x, BACK_Y - 0.4, 6.2))

# Visible cove LED lips (the hidden emitters that actually light the bake are below)
for s in (-1, 1):
    emis(bm_quad((s * (HALF_W - 0.6), (BACK_Y + FRONT_WALL_Y) / 2, 8.54), (0, 0, -1), (0, 1, 0), 0.02, BACK_Y - FRONT_WALL_Y), (1.0, 0.72, 0.45))
emis(bm_quad((0, BACK_Y - 0.6, 8.54), (0, 0, -1), (1, 0, 0), 2 * HALF_W - 1.2, 0.02), (1.0, 0.72, 0.45))

for k, acc in A.items():
    acc.build(f"Theater_{k}_Room", MATS[k])
EMIS.build("Theater_Emissive_Room", MAT_EMISSIVE, with_color=True)

# ══════════════════════════════════════════════════════════════════════════
#  Bake-only lighting (not exported)
# ══════════════════════════════════════════════════════════════════════════
def emitter(name, bm, mat):
    me = bpy.data.meshes.new(name); bm.to_mesh(me); bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me); C_BAKE.objects.link(ob)
    ob.visible_camera = False
    return ob

# Cove up-lights (on top of the soffits) and down wash (under the soffit at the wall)
for s in (-1, 1):
    emitter(f"CoveUp_{s}", bm_quad((s * (HALF_W - 0.45), (BACK_Y + FRONT_WALL_Y) / 2, 9.26), (0, 0, 1), (0, 1, 0), 0.08, BACK_Y - FRONT_WALL_Y - 0.6), MAT_COVE)
    emitter(f"CoveDown_{s}", bm_quad((s * (HALF_W - 0.5), (BACK_Y + FRONT_WALL_Y) / 2, 8.53), (0, 0, -1), (0, 1, 0), 0.05, BACK_Y - FRONT_WALL_Y - 0.6), MAT_COVE)
emitter("CoveUp_Back", bm_quad((0, BACK_Y - 0.45, 9.26), (0, 0, 1), (1, 0, 0), 2 * HALF_W - 1.6, 0.08), MAT_COVE)
emitter("CoveDown_Back", bm_quad((0, BACK_Y - 0.5, 8.53), (0, 0, -1), (1, 0, 0), 2 * HALF_W - 1.6, 0.05), MAT_COVE)

def light(name, kind, loc, power, color, radius=0.05, rot=None, spot=None):
    ld = bpy.data.lights.new(name, kind)
    ld.energy = power; ld.color = color
    if hasattr(ld, "shadow_soft_size"): ld.shadow_soft_size = radius
    if spot: ld.spot_size = math.radians(spot); ld.spot_blend = 0.6
    ob = bpy.data.objects.new(name, ld); ob.location = loc
    if rot: ob.rotation_euler = rot
    C_BAKE.objects.link(ob)
    return ob

for i, p in enumerate(STEP_LIGHTS):
    light(f"Step_{i}", 'POINT', p, 0.35, AMBER, 0.03)
for i, (x, y) in enumerate(DOWNLIGHTS):
    light(f"Down_{i}", 'SPOT', (x, y, CEIL - 0.05), 25.0, (1.0, 0.86, 0.68), 0.06, (0, 0, 0), 50)
for i, p in enumerate(SCONCES):
    light(f"Sconce_{i}", 'POINT', p, 14.0, (1.0, 0.75, 0.48), 0.08)
for i, p in enumerate(EXIT_LIGHTS):
    light(f"Exit_{i}", 'POINT', p, 1.2, (0.1, 1.0, 0.35), 0.1)
for i, p in enumerate(BOOTH):
    light(f"Booth_{i}", 'POINT', p, 3.0, (0.8, 0.88, 1.0), 0.15)

# Bake-only screen emitter at the maximum screen size (16 x 9, bottom at -0.6)
scr_mat = emission_material("M_Screen", False, (1, 1, 1), 1.0)
scr = emitter("BakeScreen", bm_quad((0, SCREEN_Y, -0.6 + 4.5), (0, 1, 0), UP, 16.0, 9.0, (0, 0, 1, 1)), scr_mat)
scr.visible_camera = True

# ══════════════════════════════════════════════════════════════════════════
#  Lightmap UVs: one shared atlas across all exported meshes
# ══════════════════════════════════════════════════════════════════════════
meshes = [o for o in C_EXPORT.objects if o.type == 'MESH' and not o.name.startswith("Theater_Emissive")]
for o in meshes:
    o.data.uv_layers.new(name="Lightmap")
    o.data.uv_layers.active = o.data.uv_layers["Lightmap"]
bpy.ops.object.select_all(action='DESELECT')
for o in meshes: o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
bpy.ops.object.mode_set(mode='EDIT')
bpy.ops.mesh.select_all(action='SELECT')
bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=0.0, area_weight=0.0,
                         correct_aspect=True, scale_to_bounds=False)
bpy.ops.uv.select_all(action='SELECT')
bpy.ops.uv.pack_islands(rotate=True, margin=0.0025)
bpy.ops.object.mode_set(mode='OBJECT')
for o in meshes:
    o.data.uv_layers.active = o.data.uv_layers["UVMap"]

# ── Stats + metadata ──────────────────────────────────────────────────────
tris = 0
for o in C_EXPORT.objects:
    if o.type == 'MESH':
        o.data.calc_loop_triangles()
        t = len(o.data.loop_triangles); tris += t
        print(f"[theater] {o.name:34s} {t:7d} tris  {len(o.data.vertices):7d} verts")
print(f"[theater] TOTAL TRIS {tris}  seats {len(seats)}")
with open(SRC + "/layout.json", "w") as f:
    json.dump({"triangles": tris, "seats": anchors}, f, indent=1)

bpy.ops.wm.save_as_mainfile(filepath=SRC + "/theater.blend", compress=True)
print("[theater] saved", SRC + "/theater.blend")
