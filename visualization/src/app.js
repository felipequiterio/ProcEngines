import * as THREE from 'https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.module.min.js';

const PE = window.PE;
const { PROPS, CYCLES, CYCLE_ORDER, PROP_ORDER, FUELS, TIERS, fmt } = PE;
const $ = id => document.getElementById(id);

/* ───────────────────────── State & presets ───────────────────────── */
const PRESETS = {
  merlin:  { name: 'Merlin 1D-like', prop: 'Kerolox', cycle: 'GasGenerator', pc: 97, eps: 16, thrust: 914, lf: 0.8, alt: 0, cluster: 1 },
  octaweb: { name: 'Octaweb: 9 × Merlin', prop: 'Kerolox', cycle: 'GasGenerator', pc: 97, eps: 16, thrust: 914, lf: 0.8, alt: 0, cluster: 9 },
  mvac:    { name: 'Merlin Vacuum-like', prop: 'Kerolox', cycle: 'GasGenerator', pc: 97, eps: 165, thrust: 981, lf: 0.8, alt: 120, cluster: 1 },
  kestrel: { name: 'Kestrel-like (pressure-fed)', prop: 'Kerolox', cycle: 'PressureFed', pc: 9, eps: 60, thrust: 31, lf: 0.8, alt: 120, cluster: 1 },
  ruth:    { name: 'Rutherford-like (electric)', prop: 'Kerolox', cycle: 'Electric', pc: 30, eps: 12, thrust: 25, lf: 0.8, alt: 0, cluster: 1 },
  rl10:    { name: 'RL10-like (expander)', prop: 'Hydrolox', cycle: 'Expander', pc: 44, eps: 84, thrust: 110, lf: 0.85, alt: 120, cluster: 1 },
  rs25:    { name: 'RS-25-like (staged)', prop: 'Hydrolox', cycle: 'Staged', pc: 206, eps: 69, thrust: 2279, lf: 0.8, alt: 0, cluster: 1 },
  rd180:   { name: 'RD-180-like (2 chambers)', prop: 'Kerolox', cycle: 'Staged', pc: 257, eps: 36, thrust: 2076, lf: 0.8, alt: 0, cluster: 2 },
  raptor:  { name: 'Raptor 2-like (full-flow)', prop: 'Methalox', cycle: 'FullFlow', pc: 300, eps: 34, thrust: 2450, lf: 0.8, alt: 0, cluster: 1 },
  nerva:   { name: 'NERVA-like (nuclear)', prop: 'NTR-H2', cycle: 'Nuclear', pc: 31, eps: 100, thrust: 250, lf: 0.8, alt: 120, cluster: 1, fuel: 'Graphite' },
};
const CYCLE_PRESET = { PressureFed: 'kestrel', Electric: 'ruth', GasGenerator: 'merlin', Expander: 'rl10', Staged: 'rs25', FullFlow: 'raptor', Nuclear: 'nerva' };

const state = Object.assign({ tier: -1, fuel: 'Graphite', plume: true, exploded: false, cut: false, labels: true, spin: true }, PRESETS.merlin);
let lastEdit = 'preset';
let ev = null;

/* ───────────────────────── Renderer ───────────────────────── */
const canvas = $('gl');
const view = $('view');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 1.05;
renderer.localClippingEnabled = true;
renderer.setClearColor(0x000000, 0);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(32, 1, 0.01, 2000);

function makeEnvironment(r) {
  const env = new THREE.Scene();
  const sky = new THREE.Mesh(new THREE.SphereGeometry(50, 32, 16), new THREE.ShaderMaterial({
    side: THREE.BackSide, depthWrite: false,
    vertexShader: 'varying vec3 vP; void main(){ vP = position; gl_Position = projectionMatrix*modelViewMatrix*vec4(position,1.); }',
    fragmentShader: 'varying vec3 vP; void main(){ float h = normalize(vP).y; vec3 top = vec3(0.55,0.58,0.62); vec3 mid = vec3(0.16,0.17,0.19); vec3 bot = vec3(0.05,0.045,0.04); vec3 c = h > 0. ? mix(mid, top, pow(h, .7)) : mix(mid, bot, pow(-h, .5)); gl_FragColor = vec4(c, 1.); }',
  }));
  env.add(sky);
  const panel = (w, h, pos, intensity, color = 0xffffff) => {
    const m = new THREE.Mesh(new THREE.PlaneGeometry(w, h), new THREE.MeshBasicMaterial({ color: new THREE.Color(color).multiplyScalar(intensity), side: THREE.DoubleSide }));
    m.position.copy(pos); m.lookAt(0, 0, 0); env.add(m);
  };
  panel(30, 10, new THREE.Vector3(10, 30, 25), 4.0);
  panel(14, 30, new THREE.Vector3(-35, 6, 8), 1.6, 0xd8e4ff);
  panel(20, 6, new THREE.Vector3(18, -4, -34), 1.4, 0xffd2b0);
  const pm = new THREE.PMREMGenerator(r);
  const tex = pm.fromScene(env, 0.035).texture;
  pm.dispose();
  return tex;
}
scene.environment = makeEnvironment(renderer);
const key = new THREE.DirectionalLight(0xfff1e0, 1.4); key.position.set(4, 8, 6); scene.add(key);
const rim = new THREE.DirectionalLight(0x9fc3ff, 0.6); rim.position.set(-6, 2, -5); scene.add(rim);
scene.add(new THREE.HemisphereLight(0x8a96a3, 0x1a1512, 0.35));

/* ───────────────────────── Procedural textures ───────────────────────── */
function canvasTex(w, h, draw, srgb = true) {
  const c = document.createElement('canvas'); c.width = w; c.height = h;
  draw(c.getContext('2d'), w, h);
  const t = new THREE.CanvasTexture(c);
  if (srgb) t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = 8;
  return t;
}
const TUBES_PER_TILE = 8;
const tubeTex = canvasTex(512, 8, (g, w, h) => {
  const per = w / TUBES_PER_TILE;
  for (let x = 0; x < w; x++) {
    const u = ((x % per) / per) * 2 - 1;
    const prof = Math.sqrt(Math.max(0, 1 - u * u));
    const v = Math.round(40 + 215 * Math.pow(prof, 0.8) * (Math.abs(u) > 0.93 ? 0.25 : 1));
    g.fillStyle = `rgb(${v},${v},${v})`; g.fillRect(x, 0, 1, h);
  }
});
tubeTex.wrapS = THREE.RepeatWrapping;
const tubeBump = tubeTex.clone(); tubeBump.colorSpace = THREE.NoColorSpace; tubeBump.wrapS = THREE.RepeatWrapping; tubeBump.needsUpdate = true;
const glowTex = canvasTex(4, 256, (g, w, h) => {
  const gr = g.createLinearGradient(0, 0, 0, h);
  gr.addColorStop(0, '#ffffff'); gr.addColorStop(0.25, '#b0b0b0'); gr.addColorStop(0.65, '#303030'); gr.addColorStop(1, '#000000');
  g.fillStyle = gr; g.fillRect(0, 0, w, h);
});
const injTex = canvasTex(256, 256, (g, w, h) => {
  g.fillStyle = '#3a3633'; g.fillRect(0, 0, w, h);
  const cx = w / 2, cy = h / 2;
  for (let ring = 1; ring <= 7; ring++) {
    const rr = ring * 16.5, n = Math.round(ring * 7.5);
    for (let i = 0; i < n; i++) {
      const a = i / n * Math.PI * 2 + ring * 0.3;
      g.beginPath(); g.arc(cx + Math.cos(a) * rr, cy + Math.sin(a) * rr, 3.2, 0, Math.PI * 2);
      g.fillStyle = '#b9b2a8'; g.fill();
      g.beginPath(); g.arc(cx + Math.cos(a) * rr, cy + Math.sin(a) * rr, 1.3, 0, Math.PI * 2);
      g.fillStyle = '#141210'; g.fill();
    }
  }
  g.beginPath(); g.arc(cx, cy, 9, 0, Math.PI * 2); g.fillStyle = '#9d968c'; g.fill();
});

/* ───────────────────────── Materials ───────────────────────── */
const std = (color, metal, rough, extra = {}) => new THREE.MeshStandardMaterial(Object.assign({ color, metalness: metal, roughness: rough }, extra));
const M = {
  jacket: std(0x9aa0a6, 0.9, 0.32),
  tubes: std(0xb98258, 0.92, 0.36, { map: tubeTex, bumpMap: tubeBump, bumpScale: 1.2 }),
  tubesSteel: std(0xa9adb2, 0.92, 0.34, { map: tubeTex, bumpMap: tubeBump, bumpScale: 1.2 }),
  soot: std(0x4a3d34, 0.35, 0.8, { side: THREE.DoubleSide }),
  niobium: std(0xc8c2b8, 0.85, 0.38, { emissive: 0xff4a0a, emissiveMap: glowTex, emissiveIntensity: 0 }),
  cc: std(0x2a2826, 0.25, 0.72, { emissive: 0xff3a14, emissiveMap: glowTex, emissiveIntensity: 0 }),
  steel: std(0x8d939a, 0.85, 0.38),
  dark: std(0x4a5056, 0.8, 0.45),
  alu: std(0xc5c9ce, 0.9, 0.26),
  hot: std(0x7a6556, 0.85, 0.36),
  line: std(0xa8aeb4, 0.9, 0.3),
  foam: std(0xe7e1d3, 0.0, 0.85),
  copv: std(0x2c2e31, 0.3, 0.5),
  valve: std(0x3f6fa6, 0.6, 0.35),
  motor: std(0x2d3237, 0.7, 0.45),
  cable: std(0xe2731c, 0.1, 0.6),
  box: std(0x3a4046, 0.6, 0.5),
  drum: std(0xd4d6d9, 0.75, 0.32),
  absorber: std(0x1b1c1e, 0.4, 0.6),
  shield: std(0x6f757b, 0.85, 0.42),
  reactor: std(0xb3b9bf, 0.9, 0.3),
  injector: std(0xffffff, 0.6, 0.55, { map: injTex, side: THREE.DoubleSide }),
  plate: std(0x7e848b, 0.85, 0.4),
};
const CLIPPABLE = [M.jacket, M.tubes, M.tubesSteel, M.soot, M.niobium, M.cc, M.reactor, M.steel, M.injector];
const cutPlane = new THREE.Plane(new THREE.Vector3(0, 0, -1), 0);

/* ───────────────────────── Part info (tooltips + labels) ───────────────────────── */
const INFO = {
  mount: ['Mount and thrust frame', 'Top attach node. Matches the stack diameter and carries thrust into the vessel.'],
  gimbal: ['Gimbal block', 'Pivot for ModuleGimbal; thrustTransform is re-parented under it so thrust follows the gimbal (§8.1).'],
  actuator: ['Gimbal actuators', 'Piston pairs. Range 0–8°, mass ∝ range × thrust.'],
  injector: ['Injector and dome', 'Spherical cap over the injector face. Manifold torus scales with Rc (§8.3).'],
  chamber: ['Combustion chamber', 'Rc = √εc·Rt; length from V_c = L*·At (§8.3).'],
  bell: ['Regen-cooled bell', 'Rao bell contour. Propellant runs through these tubes before it reaches the injector.'],
  ext: ['Radiative extension', 'Niobium or carbon-carbon, starts at ε_regen. Thin and light, glows when hot. Vacuum designs only (ε > 40).'],
  manifold: ['Regen manifold', 'Coolant manifold torus where the cooled section ends (§8.3).'],
  feed: ['Propellant lines', 'Swept tubes on a Catmull–Rom spline, diameter ∝ √(ṁ/ρ) (§8.5).'],
  turbopump: ['Turbopump', 'Housing, two volutes and the turbine between them. Size ∝ (pump power)^1/3.'],
  gg: ['Gas generator', 'Burns a little propellant fuel-rich to drive the turbine. That flow is dumped, costing ~2 % Isp.'],
  exhaust: ['Turbine exhaust duct', 'Ends in its own small nozzle beside the bell. Waterfall anchors the GG plume here.'],
  preburner: ['Preburner', 'Partial combustion upstream of the turbine. All of its gas goes on into the main chamber.'],
  hotgas: ['Hot-gas duct', 'Turbine exhaust routed into the injector. No dump, so no Isp loss.'],
  pump: ['Electric pump', 'Pump driven by its own motor, no turbine.'],
  motor: ['Motor can', 'Brushless motor. Mass = P / (5 kW/kg) at start tech, 8 kW/kg late.'],
  inverter: ['Inverter and battery box', 'ElectricCharge is a third PROPELLANT: P = ṁ·Δp / (ρ·η_pump·η_motor).'],
  cable: ['Power cables', 'High-voltage lines from the inverter to the motors.'],
  bottle: ['Helium bottles', 'Pressurant mass term, sized for a 400 s rated burn (§5.5). Keeps pressure-fed from being free.'],
  valve: ['Main valves', 'Propellant valves straight off the tank feed. No pumps in this cycle.'],
  reactor: ['Reactor vessel', 'Graphite or CERMET core heats hydrogen to Tc. Mass ≈ m_core0 + k_th·P_th (§5.4).'],
  shield: ['Shield disc', 'Optional internal shield, ~10 % mass. Cuts radiation toward the vessel (Kerbalism emitter).'],
  drums: ['Control drums', 'Rotating drums with an absorber face set reactivity. Spool-up takes seconds.'],
  plate: ['Cluster thrust plate', 'One part, N engines: one ModuleEnginesFX with N thrustTransforms (§16.3).'],
};
function tipExtra(k) {
  if (!ev) return '';
  switch (k) {
    case 'turbopump': return `${fmt(ev.PpumpKW / 1000, 2)} MW shaft power · Δp ${fmt(ev.cyc.dp * ev.d.pc + 10, 0)} bar`;
    case 'pump': return `${fmt(ev.PpumpKW, 0)} kW for both pumps`;
    case 'inverter': case 'motor': return `${fmt(ev.ecDraw, 0)} EC/s while running`;
    case 'bottle': return `≈ ${fmt(ev.mass.press || 0, 0)} kg pressurant system`;
    case 'reactor': return `${fmt(ev.Pth, 0)} MW thermal · Tc ${ev.fuel ? ev.fuel.tc : 0} K`;
    case 'chamber': return `Rc ${fmt(ev.Rc * 100, 1)} cm · εc ${fmt(ev.epsC, 2)} · Pc ${fmt(ev.d.pc, 0)} bar`;
    case 'bell': return `ε 1 → ${fmt(ev.regenEps, 0)} · θn ${fmt(ev.cone ? 15 : ev.angles.thetaN, 1)}°`;
    case 'ext': return `ε ${fmt(ev.regenEps, 0)} → ${fmt(ev.d.eps, 0)} · ${ev.extMat}`;
    case 'exhaust': return `≈ ${fmt(ev.mdot * 0.03, 1)} kg/s dumped`;
    case 'gimbal': return `±5° default`;
    case 'feed': return `ṁ ${fmt(ev.mdot, 1)} kg/s total`;
    case 'plate': return `${state.cluster} × ${fmt(ev.thrustVac / 1000, 0)} kN`;
    default: return '';
  }
}

/* ───────────────────────── Geometry helpers ───────────────────────── */
const V3 = (x, y, z) => new THREE.Vector3(x, y, z);
function rotY(v, a) { const c = Math.cos(a), s = Math.sin(a); return V3(v.x * c - v.z * s, v.y, v.x * s + v.z * c); }
function polar(a, r, y) { return V3(r * Math.cos(a), y, r * Math.sin(a)); }
function cylBetween(p0, p1, r, mat, rEnd = r, seg = 14) {
  const d = new THREE.Vector3().subVectors(p1, p0);
  const len = d.length();
  const m = new THREE.Mesh(new THREE.CylinderGeometry(rEnd, r, len, seg, 1), mat);
  m.position.copy(p0).addScaledVector(d, 0.5);
  m.quaternion.setFromUnitVectors(V3(0, 1, 0), d.normalize());
  return m;
}
function torus(R, r, y, mat, seg = 64) {
  const m = new THREE.Mesh(new THREE.TorusGeometry(R, r, 10, seg), mat);
  m.rotation.x = Math.PI / 2; m.position.y = y; return m;
}
function tubeMesh(points, r, mat) {
  const curve = new THREE.CatmullRomCurve3(points, false, 'centripetal');
  return new THREE.Mesh(new THREE.TubeGeometry(curve, Math.max(28, points.length * 14), r, 10, false), mat);
}
function lathe(pts, off, mat, segs = 64) {
  const v = pts.map(p => new THREE.Vector2(Math.max(1e-4, p.r + off), -p.x)).reverse();
  return new THREE.Mesh(new THREE.LatheGeometry(v, segs), mat);
}

/* ───────────────────────── Engine builder (§8) ───────────────────────── */
function buildEngine(ev, opt = {}) {
  const C = PE.contour(ev);
  const { Rt, Rc } = C;
  const root = new THREE.Group();
  const groups = {};
  const anchors = [];
  const grp = (name, ex = [0, 0, 0]) => {
    if (!groups[name]) { const g = new THREE.Group(); g.userData.explode = ex; g.userData.gname = name; root.add(g); groups[name] = g; }
    return groups[name];
  };
  const add = (gname, mesh, key) => { mesh.userData.key = key; grp(gname).add(mesh); return mesh; };
  const anchor = (key, gname, p, normal) => anchors.push({ key, gname, p, n: normal || V3(p.x, 0, p.z).normalize(), sym: !normal ? { r: Math.hypot(p.x, p.z), y: p.y } : null });

  const tJ = 0.05 * Rc, tB = 0.03 * Rt + 0.012 * Rc, tE = 0.012 * Rt;
  const yInj = -C.xInj;
  const yc = -C.xc;
  const exitY = -C.E.x;
  const aF = 0.75; // label-facing angle

  // Section explode offsets
  grp('mount', [0, 2.0 * Rc, 0]);
  grp('injector', [0, 1.0 * Rc, 0]);
  grp('chamber', [0, 0, 0]);
  grp('bell', [0, -0.7 * Rc, 0]);
  grp('ext', [0, -0.9 * Rc - 0.18 * C.E.x, 0]);
  grp('plumbing');

  // Outer contour radius at height y (for routing ducts down the bell)
  const all = [...C.pts.chamber, ...C.pts.conv, ...C.pts.throat, ...C.pts.bell, ...C.pts.ext];
  const rOuter = y => {
    const x = -y;
    if (x <= all[0].x) return Rc + tJ;
    for (let i = 1; i < all.length; i++) {
      if (x <= all[i].x) { const a = all[i - 1], b = all[i]; const f = (x - a.x) / Math.max(1e-9, b.x - a.x); return a.r + f * (b.r - a.r) + tB; }
    }
    return C.Re + tB;
  };

  // Chamber + convergent + throat
  const chPts = [...C.pts.chamber, ...C.pts.conv, ...C.pts.throat];
  const chMat = ev.nuclear ? M.reactor : M.jacket;
  add('chamber', lathe(chPts, tJ, chMat), ev.nuclear ? 'reactor' : 'chamber');
  add('chamber', lathe(chPts, 0, M.soot), ev.nuclear ? 'reactor' : 'chamber');
  add('chamber', torus(C.N.r + tJ * 0.8, tJ * 0.55, -C.N.x, M.dark), 'chamber');
  anchor(ev.nuclear ? 'reactor' : 'chamber', 'chamber', polar(aF, Rc + tJ, yInj - 0.5 * C.cylLen));
  if (!ev.nuclear) anchor('throat', 'chamber', polar(aF, C.Rt + tJ, 0));

  // Bell (regen) and extension
  const tubeMat = (ev.d.prop === 'Hydrolox' || ev.nuclear) ? M.tubesSteel : M.tubes;
  const nTubes = Math.round(Math.min(96, Math.max(48, 36 + 16 * Math.log10(ev.thrustVac / 1000 + 1))) / TUBES_PER_TILE) * TUBES_PER_TILE;
  tubeMat.map.repeat.set(nTubes / TUBES_PER_TILE, 1);
  tubeBump.repeat.set(nTubes / TUBES_PER_TILE, 1);
  add('bell', lathe(C.pts.bell, tB, ev.cone ? M.jacket : tubeMat), 'bell');
  add('bell', lathe(C.pts.bell, 0, M.soot), 'bell');
  const bellMid = C.pts.bell[Math.floor(C.pts.bell.length * 0.55)];
  anchor('bell', 'bell', polar(aF, bellMid.r + tB, -bellMid.x));
  let lowGroup = 'bell';
  if (C.pts.ext.length > 1) {
    const extMat = ev.extMat === 'C-C' ? M.cc : M.niobium;
    add('ext', lathe(C.pts.ext, tE, extMat), 'ext');
    add('ext', lathe(C.pts.ext, 0, M.soot), 'ext');
    add('bell', torus(C.split.r + tB, tB * 0.9, -C.split.x, M.dark), 'manifold');
    add('ext', torus(C.Re + tE * 0.6, tE * 0.9, exitY, M.dark), 'ext');
    const em = C.pts.ext[Math.floor(C.pts.ext.length * 0.55)];
    anchor('ext', 'ext', polar(aF, em.r + tE, -em.x));
    lowGroup = 'ext';
  } else {
    add('bell', torus(C.Re + tB * 0.6, tB * 0.8, exitY, M.dark), 'bell');
  }

  // Injector dome + face + manifold
  const Rd = Rc + tJ;
  const domeH = 0.42 * Rd;
  const dome = new THREE.Mesh(new THREE.SphereGeometry(Rd, 48, 12, 0, Math.PI * 2, 0, Math.PI / 2), ev.nuclear ? M.reactor : M.steel);
  dome.scale.y = domeH / Rd; dome.position.y = yInj;
  add('injector', dome, ev.nuclear ? 'reactor' : 'injector');
  add('injector', torus(Rd, 0.075 * Rc, yInj + 0.02 * Rc, M.dark), ev.nuclear ? 'reactor' : 'injector');
  const face = new THREE.Mesh(new THREE.CircleGeometry(Rc * 0.995, 48), M.injector);
  face.rotation.x = Math.PI / 2; face.position.y = yInj - 0.002 * Rc;
  add('injector', face, 'injector');
  const yDomeTop = yInj + domeH;
  const yDome = rho => yInj + domeH * Math.sqrt(Math.max(0, 1 - (rho / Rd) ** 2));
  if (!ev.nuclear) anchor('injector', 'injector', polar(aF, Rd * 0.75, yDome(Rd * 0.75)));

  // NTR shield + drums
  let yBase = yDomeTop;
  if (ev.nuclear) {
    grp('shield', [0, 1.5 * Rc, 0]);
    const hS = 0.24 * Rc;
    const sh = new THREE.Mesh(new THREE.CylinderGeometry(1.16 * Rd, 1.16 * Rd, hS, 64), M.shield);
    sh.position.y = yDomeTop + hS / 2; add('shield', sh, 'shield');
    add('shield', torus(1.16 * Rd, 0.04 * Rc, yDomeTop + hS, M.dark), 'shield');
    anchor('shield', 'shield', polar(aF, 1.16 * Rd, yDomeTop + hS / 2));
    yBase = yDomeTop + hS;
    grp('drums', [0, 0, 0]);
    const nD = 12, rD = 0.11 * Rc, ringR = Rd + rD + 0.01 * Rc, hD = 0.86 * C.cylLen;
    const yMid = yInj - C.cylLen / 2;
    for (let i = 0; i < nD; i++) {
      const a = i / nD * Math.PI * 2;
      const d = new THREE.Mesh(new THREE.CylinderGeometry(rD, rD, hD, 18), M.drum);
      d.position.copy(polar(a, ringR, yMid)); add('drums', d, 'drums');
      const ab = new THREE.Mesh(new THREE.CylinderGeometry(rD * 1.04, rD * 1.04, hD * 0.96, 12, 1, true, 0, Math.PI * 0.75), M.absorber);
      ab.position.copy(d.position); ab.rotation.y = -a + Math.PI * (0.25 + 0.5 * Math.sin(i * 1.7)); add('drums', ab, 'drums');
    }
    add('drums', torus(ringR, rD * 0.35, yMid + hD / 2 + rD * 0.2, M.dark), 'drums');
    add('drums', torus(ringR, rD * 0.35, yMid - hD / 2 - rD * 0.2, M.dark), 'drums');
    anchor('drums', 'drums', polar(aF + 0.3, ringR + rD, yMid - hD * 0.25));
  }

  // Gimbal block, thrust frame, mount plate, actuators
  const hG = 0.3 * Rc;
  const gb = new THREE.Mesh(new THREE.CylinderGeometry(0.28 * Rc, 0.32 * Rc, hG, 32), M.dark);
  gb.position.y = yBase + hG / 2; add('mount', gb, 'gimbal');
  add('mount', torus(0.31 * Rc, 0.04 * Rc, yBase + hG * 0.5, M.steel, 32), 'gimbal');
  anchor('gimbal', 'mount', polar(aF, 0.32 * Rc, yBase + hG / 2));
  const yTop = yBase + hG + 0.5 * Rc;
  const Rm = 1.12 * Rd;
  const plate = new THREE.Mesh(new THREE.CylinderGeometry(Rm, Rm, 0.05 * Rc, 64), M.plate);
  plate.position.y = yTop; add('mount', plate, 'mount');
  for (let k = 0; k < 4; k++) {
    const a = Math.PI / 4 + k * Math.PI / 2;
    add('mount', cylBetween(polar(a, Rm * 0.82, yTop), polar(a, 0.26 * Rc, yBase + hG), 0.045 * Rc, M.steel), 'mount');
  }
  anchor('mount', 'mount', polar(aF + 0.4, Rm, yTop));
  for (const a of [Math.PI * 1.25, Math.PI * 1.75]) {
    const p0 = polar(a, Rm * 0.9, yTop - 0.02 * Rc);
    const p2 = polar(a, Rd + 0.02 * Rc, yInj - 0.3 * C.cylLen);
    const p1 = p0.clone().lerp(p2, 0.55);
    add('mount', cylBetween(p0, p1, 0.06 * Rc, M.valve), 'actuator');
    add('mount', cylBetween(p1, p2, 0.03 * Rc, M.alu), 'actuator');
  }
  anchor('actuator', 'mount', polar(Math.PI * 1.75, Rm * 0.9, (yTop + yInj) / 2));

  const ctx = { ev, C, Rt, Rc, Rd, tJ, tB, yInj, yc, exitY, yDomeTop, yDome, yBase, yTop, Rm, rOuter, add, grp, anchor, opt };
  const kit = KITS[ev.d.cycle];
  const kitOut = kit ? kit(ctx) || {} : {};

  root.userData = { groups, anchors, exitY, Re: C.Re, lowGroup, gg: kitOut.gg || null, yTop, Rm, Rc, C };
  return root;
}

function inletFromTop(ctx, p, a) {
  // propellant line from a point up to the mount plate (comes from the tank)
  const { yTop, Rm } = ctx;
  const q = polar(a, Math.min(Rm * 0.7, Math.hypot(p.x, p.z)), yTop - 0.03 * ctx.Rc);
  return [p, V3(p.x, (p.y + yTop) / 2, p.z), q];
}
function feedToManifold(ctx, p, a, s) {
  const { Rd, yInj, Rc } = ctx;
  const m = polar(a, Rd + 0.06 * Rc, yInj + 0.02 * Rc);
  const outDir = V3(m.x - p.x, 0, m.z - p.z).normalize();
  return [p, p.clone().addScaledVector(outDir, 0.25 * s).add(V3(0, 0.1 * s, 0)), m.clone().add(V3(0, 0.22 * Rc, 0)).addScaledVector(outDir, -0.15 * Rc), m];
}
function pumpScale(ctx, kw) {
  return Math.min(1.5 * ctx.Rc, Math.max(0.3 * ctx.Rc, 0.0105 * Math.cbrt(Math.max(kw, 1))));
}

// Turbopump: vertical axis, top volute = ox pump, bottom volute = fuel pump, turbine between.
function turbopump(ctx, gname, a, ringR, yC, s, o = {}) {
  const g = new THREE.Group();
  const mk = (geo, mat, key = 'turbopump') => { const m = new THREE.Mesh(geo, mat); m.userData.key = key; g.add(m); return m; };
  mk(new THREE.CylinderGeometry(0.46 * s, 0.46 * s, 0.42 * s, 28), M.alu).position.y = 0.55 * s;
  const v1 = mk(new THREE.TorusGeometry(0.46 * s, 0.2 * s, 12, 36), M.alu); v1.rotation.x = Math.PI / 2; v1.position.y = 0.5 * s;
  mk(new THREE.CylinderGeometry(0.36 * s, 0.4 * s, 0.5 * s, 28), M.hot).position.y = 0;
  const tm = mk(new THREE.TorusGeometry(0.42 * s, 0.11 * s, 10, 32), M.hot); tm.rotation.x = Math.PI / 2; tm.position.y = -0.04 * s;
  mk(new THREE.CylinderGeometry(0.44 * s, 0.44 * s, 0.38 * s, 28), M.alu).position.y = -0.55 * s;
  const v2 = mk(new THREE.TorusGeometry(0.44 * s, 0.18 * s, 12, 36), M.alu); v2.rotation.x = Math.PI / 2; v2.position.y = -0.56 * s;
  mk(new THREE.SphereGeometry(0.2 * s, 16, 10), M.dark).position.y = -0.82 * s;
  if (o.topInlet !== false) {
    mk(new THREE.CylinderGeometry(0.2 * s, 0.24 * s, 0.4 * s, 20), M.alu).position.y = 0.95 * s;
    const fl = mk(new THREE.TorusGeometry(0.22 * s, 0.05 * s, 8, 24), M.dark); fl.rotation.x = Math.PI / 2; fl.position.y = 1.15 * s;
  }
  const center = polar(a, ringR, yC);
  g.position.copy(center); g.rotation.y = -a;
  ctx.grp(gname).add(g);
  const w = l => rotY(l, a).add(center);
  return {
    center, s,
    oxOut: w(V3(-0.5 * s, 0.5 * s, 0.25 * s)),
    fuelOut: w(V3(-0.5 * s, -0.56 * s, -0.25 * s)),
    turbOut: w(V3(0.42 * s, -0.02 * s, 0)),
    turbSide: w(V3(0, 0, 0.42 * s)),
    top: w(V3(0, 1.15 * s, 0)),
    topNoInlet: w(V3(0, 0.78 * s, 0)),
    sideUp: w(V3(0.25 * s, 0.6 * s, 0.4 * s)),
  };
}
function kitGroupExplode(a, d, up = 0) { return [Math.cos(a) * d * 1.8, up, Math.sin(a) * d * 1.8]; }

function exhaustDuct(ctx, gname, from, a, s, rDuct) {
  const { rOuter, exitY, yInj } = ctx;
  const pts = [from, from.clone().add(polar(a, 0.3 * s, 0)).setY(from.y - 0.05 * s)];
  const y0 = from.y - 0.3 * s, y1 = exitY * 0.5;
  for (let i = 0; i <= 5; i++) {
    const y = y0 + (y1 - y0) * i / 5;
    pts.push(polar(a + 0.12, rOuter(y) + rDuct * 1.6 + 0.04 * ctx.Rc, y));
  }
  const m = tubeMesh(pts, rDuct, M.hot); m.userData.key = 'exhaust'; ctx.grp(gname).add(m);
  const end = pts[pts.length - 1];
  const dir = new THREE.Vector3().subVectors(end, pts[pts.length - 2]).normalize();
  const nzLen = rDuct * 3.2;
  const tip = end.clone().addScaledVector(dir, nzLen);
  const nz = cylBetween(end, tip, rDuct, M.dark, rDuct * 1.9, 18);
  nz.material = M.dark; nz.userData.key = 'exhaust'; ctx.grp(gname).add(nz);
  // open the nozzle end: geometry is capped; fine at this scale
  ctx.anchor('exhaust', gname, pts[3], polar(a, 1, 0));
  return { pos: tip, dir, r: rDuct * 1.9 };
}

const KITS = {
  PressureFed(ctx) {
    const { ev, Rc, Rd, yInj, yTop, yDomeTop, yDome, Rm } = ctx;
    const rb = Math.min(1.0 * Rc, Math.max(0.36 * Rc, 0.06 * Math.cbrt(ev.mass.press || 50) * (Rc / Math.max(Rc, 0.25)) + 0.2 * Rc));
    [0, Math.PI].forEach((a, i) => {
      const gname = 'kit' + i; ctx.grp(gname, kitGroupExplode(a, 1.2 * rb, 0.2 * Rc));
      const c = polar(a, Rd + rb + 0.12 * Rc, yInj - rb * 0.1);
      const b = new THREE.Mesh(new THREE.SphereGeometry(rb, 32, 20), M.copv); b.position.copy(c); ctx.add(gname, b, 'bottle');
      const band = torus(rb * 1.005, rb * 0.04, c.y, M.steel, 48); band.position.x = c.x; band.position.z = c.z; ctx.add(gname, band, 'bottle');
      ctx.add(gname, cylBetween(c.clone().add(V3(0, rb * 0.9, 0)), polar(a, Math.min(Rm * 0.95, Math.hypot(c.x, c.z)), yTop), 0.04 * Rc, M.steel), 'bottle');
      ctx.add('plumbing', tubeMesh([c.clone().add(polar(a + Math.PI / 2, rb * 0.5, rb * 0.7)), polar(a + 0.5, Rm * 0.8, (c.y + yTop) / 2 + 0.1 * Rc), polar(a + 0.5, Rm * 0.6, yTop)], 0.025 * Rc, M.line), 'bottle');
      if (i === 0) ctx.anchor('bottle', gname, c.clone().add(polar(a, rb, 0)), polar(a, 1, 0));
    });
    [Math.PI / 2, Math.PI * 1.5].forEach((a, i) => {
      const p = polar(a, Rd * 0.55, yDome(Rd * 0.55));
      const vb = new THREE.Mesh(new THREE.BoxGeometry(0.24 * Rc, 0.2 * Rc, 0.24 * Rc), M.valve);
      vb.position.copy(p).add(V3(0, 0.1 * Rc, 0)); ctx.add('injector', vb, 'valve');
      ctx.add('injector', cylBetween(vb.position.clone().add(polar(a, 0.12 * Rc, 0)), vb.position.clone().add(polar(a, 0.3 * Rc, 0.05 * Rc)), 0.05 * Rc, M.dark), 'valve');
      ctx.add('plumbing', tubeMesh([vb.position.clone().add(V3(0, 0.1 * Rc, 0)), vb.position.clone().add(V3(0, 0.3 * Rc, 0)), polar(a, Rd * 0.6, yTop - 0.03 * Rc)], 0.08 * Rc, ev.d.prop === 'Hydrolox' && i === 0 ? M.foam : M.line), 'feed');
      if (i === 0) ctx.anchor('valve', 'injector', vb.position.clone(), polar(a, 1, 0));
    });
  },

  Electric(ctx) {
    const { ev, Rc, Rd, yInj, yTop, Rm } = ctx;
    const s = Math.max(0.5 * Rc, pumpScale(ctx, ev.PpumpKW / 2) * 1.05);
    const motors = [];
    [0, Math.PI].forEach((a, i) => {
      const gname = 'kit' + i; ctx.grp(gname, kitGroupExplode(a, 1.4 * s, 0.1 * Rc));
      const ringR = Rd + 0.62 * s;
      const yC = yInj - 0.2 * s;
      const c = polar(a, ringR, yC);
      const v = torus(0.42 * s, 0.17 * s, yC, M.alu, 36); v.position.x = c.x; v.position.z = c.z; ctx.add(gname, v, 'pump');
      const h = new THREE.Mesh(new THREE.CylinderGeometry(0.42 * s, 0.42 * s, 0.32 * s, 28), M.alu); h.position.copy(c); ctx.add(gname, h, 'pump');
      const mc = new THREE.Mesh(new THREE.CylinderGeometry(0.36 * s, 0.36 * s, 0.85 * s, 28), M.motor); mc.position.copy(c).add(V3(0, 0.6 * s, 0)); ctx.add(gname, mc, 'motor');
      for (let f = 0; f < 4; f++) { const fin = torus(0.37 * s, 0.025 * s, c.y + 0.32 * s + f * 0.17 * s, M.dark, 28); fin.position.x = c.x; fin.position.z = c.z; ctx.add(gname, fin, 'motor'); }
      const cap = new THREE.Mesh(new THREE.CylinderGeometry(0.2 * s, 0.3 * s, 0.12 * s, 20), M.dark); cap.position.copy(c).add(V3(0, 1.08 * s, 0)); ctx.add(gname, cap, 'motor');
      motors.push(cap.position.clone());
      const out = c.clone().add(polar(a + Math.PI, 0.5 * s, 0));
      ctx.add('plumbing', tubeMesh(feedToManifold(ctx, out, a + (i ? -0.5 : 0.5), s), Math.max(0.05 * Rc, 0.12 * s), M.line), 'feed');
      const inl = c.clone().add(polar(a + 0.9, 0.5 * s, -0.05 * s));
      ctx.add('plumbing', tubeMesh([inl, inl.clone().add(polar(a + 0.9, 0.3 * s, 0.4 * s)), polar(a + 0.7, Rm * 0.8, yTop - 0.03 * Rc)], Math.max(0.05 * Rc, 0.12 * s), M.line), 'feed');
      if (i === 0) { ctx.anchor('pump', gname, c.clone().add(polar(a, 0.6 * s, 0)), polar(a, 1, 0)); ctx.anchor('motor', gname, mc.position.clone().add(polar(a, 0.36 * s, 0.1 * s)), polar(a, 1, 0)); }
    });
    ctx.grp('kitB', kitGroupExplode(Math.PI / 2, 1.0 * Rc, 0.3 * Rc));
    const a = Math.PI / 2;
    const bx = new THREE.Mesh(new THREE.BoxGeometry(0.75 * Rc, 0.32 * Rc, 0.4 * Rc), M.box);
    bx.position.copy(polar(a, Rm * 0.82, yTop - 0.22 * Rc)); bx.rotation.y = -a + Math.PI / 2; ctx.add('kitB', bx, 'inverter');
    const bt = new THREE.Mesh(new THREE.BoxGeometry(0.5 * Rc, 0.26 * Rc, 0.3 * Rc), M.cable);
    bt.position.copy(polar(a + 0.55, Rm * 0.85, yTop - 0.18 * Rc)); bt.rotation.y = -(a + 0.55) + Math.PI / 2; ctx.add('kitB', bt, 'inverter');
    ctx.anchor('inverter', 'kitB', bx.position.clone().add(polar(a, 0.2 * Rc, 0)), polar(a, 1, 0));
    motors.forEach((m, i) => {
      const st = bx.position.clone().add(V3(0, -0.12 * Rc, 0));
      ctx.add('plumbing', tubeMesh([st, st.clone().lerp(m, 0.5).add(V3(0, 0.15 * Rc, 0)), m.clone().add(V3(0, 0.05 * Rc, 0))], 0.022 * Rc, M.cable), 'cable');
    });
  },

  GasGenerator(ctx) {
    const { ev, Rc, Rd, yInj, yTop } = ctx;
    const s = pumpScale(ctx, ev.PpumpKW);
    const a = 0.15;
    ctx.grp('kit0', kitGroupExplode(a, 1.5 * s, 0.1 * Rc));
    const ringR = Rd + 0.68 * s;
    const yC = Math.max(yInj * 0.45, yInj - 0.6 * s);
    const tp = turbopump(ctx, 'kit0', a, ringR, yC, s);
    ctx.anchor('turbopump', 'kit0', tp.center.clone().add(polar(a, 0.5 * s, 0.5 * s)), polar(a, 1, 0));
    // Gas generator beside the turbine
    const ga = a + 0.95 * s / ringR + 0.25;
    const gc = polar(ga, ringR - 0.1 * s, yC + 0.15 * s);
    const gs = new THREE.Mesh(new THREE.SphereGeometry(0.2 * s, 18, 12), M.hot); gs.position.copy(gc); ctx.add('kit0', gs, 'gg');
    ctx.add('kit0', cylBetween(gc, gc.clone().add(V3(0, -0.32 * s, 0)), 0.13 * s, M.hot, 0.1 * s), 'gg');
    ctx.add('kit0', tubeMesh([gc.clone().add(V3(0, -0.3 * s, 0)), gc.clone().lerp(tp.turbSide, 0.5).add(V3(0, -0.15 * s, 0)), tp.turbSide], 0.08 * s, M.hot), 'gg');
    ctx.add('plumbing', tubeMesh([gc.clone().add(V3(0, 0.18 * s, 0)), gc.clone().add(V3(0, 0.5 * s, 0)), polar(ga - 0.1, Rd * 0.9, yTop - 0.03 * Rc)], 0.035 * s + 0.01 * Rc, M.line), 'feed');
    ctx.anchor('gg', 'kit0', gc.clone().add(polar(ga, 0.2 * s, 0)), polar(ga, 1, 0));
    const gg = exhaustDuct(ctx, 'kit0', tp.turbOut, a - 0.45, s, 0.16 * s);
    const fr = Math.max(0.05 * Rc, 0.14 * s);
    ctx.add('plumbing', tubeMesh(feedToManifold(ctx, tp.oxOut, a + 0.45, s), fr, M.line), 'feed');
    ctx.add('plumbing', tubeMesh(feedToManifold(ctx, tp.fuelOut, a - 0.5, s), fr * 0.9, ev.d.prop === 'Hydrolox' ? M.foam : M.line), 'feed');
    ctx.add('plumbing', tubeMesh(inletFromTop(ctx, tp.top, a), 0.2 * s, ev.d.prop === 'Hydrolox' ? M.line : M.line), 'feed');
    const fin = tp.fuelOut.clone().add(polar(a + 1.6, 0.3 * s, 0));
    ctx.add('plumbing', tubeMesh([tp.center.clone().add(polar(a + Math.PI / 2, 0.55 * s, -0.55 * s)), fin.clone().add(V3(0, 0.3 * s, 0)), polar(a + 1.2, ctx.Rm * 0.7, yTop - 0.03 * Rc)], fr * 0.95, ev.d.prop === 'Hydrolox' ? M.foam : M.line), 'feed');
    ctx.anchor('feed', 'plumbing', tp.oxOut.clone().lerp(polar(a + 0.45, Rd, yInj), 0.6), polar(a + 0.3, 1, 0));
    return { gg };
  },

  Expander(ctx) {
    const { ev, Rc, Rd, yInj, yTop, rOuter, C, Rt } = ctx;
    const s = pumpScale(ctx, ev.PpumpKW) * 0.95;
    const a = 0.1;
    ctx.grp('kit0', kitGroupExplode(a, 1.5 * s, 0.1 * Rc));
    const ringR = Rd + 0.65 * s;
    const yC = yInj - 0.25 * s;
    const tp = turbopump(ctx, 'kit0', a, ringR, yC, s);
    ctx.anchor('turbopump', 'kit0', tp.center.clone().add(polar(a, 0.5 * s, 0.4 * s)), polar(a, 1, 0));
    // Small ox pump opposite
    const b = Math.PI;
    ctx.grp('kit1', kitGroupExplode(b, 1.4 * s, 0.1 * Rc));
    const oc = polar(b, Rd + 0.55 * s, yInj - 0.15 * s);
    const ov = torus(0.34 * s, 0.14 * s, oc.y, M.alu, 30); ov.position.x = oc.x; ov.position.z = oc.z; ctx.add('kit1', ov, 'turbopump');
    const oh = new THREE.Mesh(new THREE.CylinderGeometry(0.33 * s, 0.3 * s, 0.5 * s, 24), M.alu); oh.position.copy(oc); ctx.add('kit1', oh, 'turbopump');
    ctx.add('plumbing', tubeMesh(feedToManifold(ctx, oc.clone().add(polar(b + Math.PI, 0.4 * s, 0)), b - 0.5, s), 0.11 * s, M.line), 'feed');
    ctx.add('plumbing', tubeMesh(inletFromTop(ctx, oc.clone().add(V3(0, 0.25 * s, 0)), b), 0.16 * s, M.line), 'feed');
    // Regen jacket manifolds: lower (coolant in) and upper chamber jacket (heated H2 out)
    const yLow = -(C.split ? C.split.x : C.E.x * 0.985);
    const rLow = rOuter(yLow);
    ctx.add('bell', torus(rLow + 0.02 * Rt, 0.14 * Rt + 0.02 * Rc, yLow, M.dark), 'manifold');
    const yUp = yInj - 0.35 * C.cylLen;
    ctx.add('chamber', torus(Rd + 0.02 * Rc, 0.08 * Rc, yUp, M.dark), 'manifold');
    ctx.anchor('manifold', 'bell', polar(0.75, rLow + 0.1 * Rt, yLow), polar(0.75, 1, 0));
    // Pump → nozzle manifold (down the bell)
    const down = [tp.fuelOut];
    const ad = a - 0.55;
    for (let i = 0; i <= 5; i++) { const y = tp.fuelOut.y - 0.3 * s + (yLow + 0.15 * Rt - (tp.fuelOut.y - 0.3 * s)) * i / 5; down.push(polar(ad, rOuter(y) + 0.1 * s + 0.03 * Rc, y)); }
    ctx.add('plumbing', tubeMesh(down, 0.1 * s, M.foam), 'feed');
    // Heated H2: chamber jacket → turbine
    const j0 = polar(a + 0.35, Rd + 0.08 * Rc, yUp);
    ctx.add('plumbing', tubeMesh([j0, j0.clone().add(polar(a + 0.35, 0.2 * s, 0.05 * s)), tp.turbSide], 0.09 * s, M.line), 'manifold');
    // Turbine exhaust → injector
    ctx.add('plumbing', tubeMesh(feedToManifold(ctx, tp.oxOut, a + 0.55, s), 0.11 * s, M.line), 'feed');
    ctx.add('plumbing', tubeMesh(inletFromTop(ctx, tp.top, a), 0.16 * s, M.foam), 'feed');
    if (ctx.opt.bleed) exhaustDuct(ctx, 'kit0', tp.turbOut, a - 0.25, s, 0.12 * s);
  },

  Staged(ctx) { return stagedKit(ctx, [0.1], 0.95); },
  FullFlow(ctx) { return stagedKit(ctx, [0.1, Math.PI + 0.1], 0.82); },

  Nuclear(ctx) {
    const { ev, Rc, Rd, yInj, yTop, rOuter, C, Rt, yBase } = ctx;
    const s = pumpScale(ctx, ev.PpumpKW) * 0.9;
    const a = 0.2;
    ctx.grp('kit0', kitGroupExplode(a, 1.5 * s, 0.2 * Rc));
    const ringR = Rd + 0.24 * Rc + 0.6 * s;
    const tp = turbopump(ctx, 'kit0', a, ringR, yInj - 0.1 * C.cylLen, s);
    ctx.anchor('turbopump', 'kit0', tp.center.clone().add(polar(a, 0.5 * s, 0.4 * s)), polar(a, 1, 0));
    const yLow = -(C.split ? C.split.x : C.E.x * 0.985);
    const rLow = rOuter(yLow);
    ctx.add('bell', torus(rLow + 0.02 * Rt, 0.12 * Rt + 0.02 * Rc, yLow, M.dark), 'manifold');
    const down = [tp.fuelOut];
    const ad = a - 0.4;
    for (let i = 0; i <= 6; i++) { const y = tp.fuelOut.y - 0.3 * s + (yLow + 0.15 * Rt - (tp.fuelOut.y - 0.3 * s)) * i / 6; down.push(polar(ad, Math.max(rOuter(y), y > -C.xc ? Rd + 0.24 * Rc : 0) + 0.1 * s + 0.03 * Rc, y)); }
    ctx.add('plumbing', tubeMesh(down, 0.1 * s, M.foam), 'feed');
    const up = [polar(a + 0.5, rLow + 0.1 * s, yLow + 0.1 * Rt)];
    for (let i = 1; i <= 5; i++) { const y = yLow + (tp.turbSide.y - yLow) * i / 5; up.push(polar(a + 0.5, Math.max(rOuter(y), y > -C.xc - 0.1 * Rc ? Rd + 0.24 * Rc : 0) + 0.1 * s + 0.03 * Rc, y)); }
    up.push(tp.turbSide);
    ctx.add('plumbing', tubeMesh(up, 0.08 * s, M.line), 'feed');
    const sh = yBase;
    ctx.add('plumbing', tubeMesh([tp.oxOut, tp.oxOut.clone().add(V3(0, 0.4 * s, 0)), polar(a + 0.15, 1.2 * Rd, sh + 0.1 * Rc), polar(a + 0.15, 0.7 * Rd, sh + 0.08 * Rc)], 0.1 * s, M.line), 'feed');
    ctx.add('plumbing', tubeMesh(inletFromTop(ctx, tp.top, a), 0.16 * s, M.foam), 'feed');
    ctx.anchor('feed', 'plumbing', down[3], polar(ad, 1, 0));
  },
};

function stagedKit(ctx, angles, k) {
  const { ev, Rc, Rd, yInj, yTop, yDome } = ctx;
  const s = pumpScale(ctx, ev.PpumpKW / angles.length) * k * 1.1;
  angles.forEach((a, i) => {
    const gname = 'kit' + i; ctx.grp(gname, kitGroupExplode(a, 1.5 * s, 0.15 * Rc));
    const ringR = Rd + 0.68 * s;
    const yC = Math.min(yInj - 0.95 * s, yInj * 0.5);
    const tp = turbopump(ctx, gname, a, ringR, yC, s, { topInlet: false });
    // Preburner on top
    const pb = tp.topNoInlet.clone().add(V3(0, 0.28 * s, 0));
    ctx.add(gname, cylBetween(tp.topNoInlet, pb.clone().add(V3(0, 0.25 * s, 0)), 0.3 * s, M.hot, 0.28 * s, 22), 'preburner');
    const cap = new THREE.Mesh(new THREE.SphereGeometry(0.28 * s, 22, 10, 0, Math.PI * 2, 0, Math.PI / 2), M.hot); cap.position.copy(pb).add(V3(0, 0.25 * s, 0)); ctx.add(gname, cap, 'preburner');
    const pbTop = cap.position.clone().add(V3(0, 0.22 * s, 0));
    // Hot-gas duct into the injector dome
    const ent = 0.5 * Rd;
    const yE = yDome(ent);
    const rDuct = 0.2 * s;
    const pts = [pbTop.clone().add(V3(0, -0.1 * s, 0)), pbTop.clone().add(V3(0, 0.25 * s, 0)), polar(a, (ringR + ent) / 2, Math.max(pbTop.y + 0.3 * s, yE + 0.55 * s)), polar(a, ent, yE + 0.25 * s), polar(a, ent * 0.98, yE - 0.02 * Rc)];
    ctx.add(gname, tubeMesh(pts, rDuct, M.hot), 'hotgas');
    // feeds
    const fr = Math.max(0.05 * Rc, 0.13 * s);
    ctx.add('plumbing', tubeMesh(feedToManifold(ctx, tp.fuelOut, a + (i ? 0.6 : -0.6), s), fr, ev.d.prop === 'Hydrolox' ? M.foam : M.line), 'feed');
    ctx.add('plumbing', tubeMesh([tp.sideUp, tp.sideUp.clone().add(polar(a + 0.8, 0.25 * s, 0.35 * s)), polar(a + 0.6, ctx.Rm * 0.75, yTop - 0.03 * Rc)], fr * 1.15, M.line), 'feed');
    ctx.add('plumbing', tubeMesh([tp.oxOut, tp.oxOut.clone().add(polar(a + Math.PI, 0.15 * s, 0.3 * s)), pb.clone().add(polar(a + Math.PI, 0.3 * s, 0))], fr * 0.6, M.line), 'feed');
    if (i === 0) {
      ctx.anchor('turbopump', gname, tp.center.clone().add(polar(a, 0.5 * s, -0.4 * s)), polar(a, 1, 0));
      ctx.anchor('preburner', gname, pb.clone().add(polar(a, 0.3 * s, 0.1 * s)), polar(a, 1, 0));
      ctx.anchor('hotgas', gname, pts[2], polar(a, 1, 0.6).normalize());
    }
  });
}

/* ───────────────────────── Plume shader (§9) ───────────────────────── */
const plumeGeo = new THREE.CylinderGeometry(1, 1, 1, 48, 140, true).translate(0, -0.5, 0);
const PLUME_VS = `
uniform float uLen, uR0, uFar, uSpread, uLam, uDia;
varying float vS; varying float vZ; varying float vFacing;
void main(){
  float s = clamp(-position.y, 0.0, 1.0);
  float z = s * uLen;
  float kz = 1.0 - exp(-z / (uR0 * 2.4 + 1e-4));
  float r = mix(uR0, uR0 * uFar, kz) + z * uSpread;
  r *= 1.0 + uDia * 0.085 * sin(6.2831 * z / uLam) * exp(-z / (uLam * 5.0));
  vec3 p = vec3(position.x * r, -z, position.z * r);
  vec4 mv = modelViewMatrix * vec4(p, 1.0);
  vec3 n = normalize(normalMatrix * vec3(position.x, 0.0, position.z));
  vFacing = abs(dot(n, normalize(-mv.xyz)));
  vS = s; vZ = z;
  gl_Position = projectionMatrix * mv;
}`;
const PLUME_FS = `
uniform vec3 uCore, uMid, uEdge; uniform float uInt, uLam, uDia, uTime, uFade;
varying float vS; varying float vZ; varying float vFacing;
void main(){
  float along = exp(-vS * uFade) * smoothstep(0.0, 0.02, vS);
  float body = pow(vFacing, 1.7);
  float d = 0.0;
  if (uDia > 0.001) { float ph = fract(vZ / uLam + 0.15); d = uDia * exp(-pow((ph - 0.5) * 6.0, 2.0)) * exp(-vZ / (uLam * 4.0)); }
  float flick = 0.88 + 0.12 * sin(vZ * 14.0 / uLam - uTime * 31.0) * sin(uTime * 11.0 + vS * 9.0);
  vec3 col = mix(uCore, uMid, smoothstep(0.0, 0.3, vS));
  col = mix(col, uEdge, smoothstep(0.3, 1.0, vS));
  col += vec3(0.9, 0.95, 1.0) * d * 0.9;
  float a = (along * body * flick + d * body * 0.85) * uInt;
  gl_FragColor = vec4(col * a, a);
}`;
const PLUME_COLORS = {
  Kerolox:  { core: 0xfff3d1, mid: 0xffad42, edge: 0xff5a14, int: 1.15, dia: 0.45, len: 1.25 },
  Methalox: { core: 0xf0eeff, mid: 0x9b8cff, edge: 0xff6f8a, int: 1.0, dia: 0.7, len: 1.1 },
  Hydrolox: { core: 0xeef8ff, mid: 0x86c9ff, edge: 0xffa6c9, int: 0.6, dia: 1.0, len: 1.0 },
  'NTR-H2': { core: 0xfff0fa, mid: 0xff9ad6, edge: 0xb48cff, int: 0.55, dia: 0.6, len: 1.0 },
  GG:       { core: 0xffcf8a, mid: 0xd0702a, edge: 0x3a2214, int: 0.9, dia: 0, len: 1.0 },
};
function plumeMaterial(c) {
  return new THREE.ShaderMaterial({
    vertexShader: PLUME_VS, fragmentShader: PLUME_FS, transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, side: THREE.DoubleSide,
    uniforms: {
      uLen: { value: 1 }, uR0: { value: 1 }, uFar: { value: 1 }, uSpread: { value: 0 }, uLam: { value: 1 }, uDia: { value: 0 },
      uCore: { value: new THREE.Color(c.core) }, uMid: { value: new THREE.Color(c.mid) }, uEdge: { value: new THREE.Color(c.edge) },
      uInt: { value: c.int }, uTime: { value: 0 }, uFade: { value: 2.5 },
    },
  });
}
function plumeParams(ev, altKm, which) {
  const pa = PE.earthPressure(altKm);
  const vac = altKm >= 119.5 || pa < 1;
  const pe = ev.pe;
  const k = vac ? 1e4 : pe / pa;
  const lk = Math.log(k);
  const sep = !vac && pe < PE.K_SEP * pa;
  const Re = ev.Re;
  const c = PLUME_COLORS[ev.d.prop];
  let r0 = Re, far, spread, len, dia, fade, inten = c.int;
  if (sep) {
    const epsS = ev.ispAt(pa).epsEff;
    r0 = Re * Math.sqrt(epsS / ev.d.eps);
  }
  if (vac) { far = 2.2; spread = Math.tan(22 * Math.PI / 180); len = 2 * Re * 5; dia = 0; fade = 2.0; inten *= 0.95; }
  else if (k >= 1) { far = 1 + Math.min(1.8, 0.42 * lk); spread = Math.tan((1.5 + Math.min(16, 3.5 * lk)) * Math.PI / 180); len = 2 * Re * (4.5 + Math.min(4, lk)); dia = Math.max(0, 1 - lk / 2.6); fade = 2.6; inten *= 1 - Math.min(0.45, 0.1 * lk); }
  else { far = Math.max(0.5, Math.pow(k, 0.35)); spread = Math.tan(1.2 * Math.PI / 180); len = 2 * r0 * (4.2 + 1.2 * k); dia = Math.max(0.25, 1 - Math.abs(lk) / 3); fade = 2.9; }
  len *= c.len;
  const lam = 1.25 * 2 * r0 * Math.sqrt(Math.min(4, Math.max(0.35, k)));
  if (which === 'core') return { r0: r0 * 0.55, far: Math.min(far, 1.4), spread: spread * 0.4, len: len * 0.55, lam, dia: dia * c.dia, fade: fade + 1, inten: inten * (vac ? 1.1 : 1.3) };
  return { r0, far, spread, len, lam, dia: dia * c.dia, fade, inten, sep, k, vac, pa };
}
function regimeOf(ev, altKm) {
  const p = plumeParams(ev, altKm, 'outer');
  if (p.vac) return ['vac', 'Vacuum · underexpanded'];
  if (p.sep) return ['sep', 'Overexpanded · flow separated'];
  if (p.k < 0.9) return ['over', 'Overexpanded'];
  if (p.k <= 1.15) return ['ideal', 'Near ideal expansion'];
  return ['under', 'Underexpanded'];
}

/* ───────────────────────── Display assembly (cluster) ───────────────────────── */
let display = null;         // THREE.Group in scene
let engines = [];           // per-engine roots
let plumes = [];            // {mesh, kind, ev}
let ggPlumes = [];
let explodeT = 0;
let fitR = 1, fitW = 1, fitCenter = V3(0, 0, 0), goalR = 1, goalW = 1, goalCenter = V3(0, 0, 0), engineBox = null;
function refit(snap) {
  if (!engineBox) return;
  const b = engineBox.clone();
  if (state.plume && !state.exploded && ev) {
    const base = display.userData.base.userData;
    const w = (b.max.x - b.min.x) / 2;
    const pl = plumeParams(ev, state.alt, 'outer');
    const extra = Math.min(pl.len * 0.5, Math.max(1.6 * ev.De, (b.max.y - b.min.y) * 0.45));
    b.expandByPoint(V3(0, base.exitY - extra, 0));
    b.expandByPoint(V3(Math.min(w * 1.1, ev.Re * pl.far * 1.3), 0, 0));
  }
  if (state.exploded && display) { const u = display.userData.base.userData; const Rc = u.Rc; b.max.y += 2.0 * Rc; b.min.y -= 0.9 * Rc + 0.18 * u.C.E.x; b.expandByVector(V3(1.6 * Rc, 0, 1.6 * Rc)); }
  const sz = b.getSize(V3(0, 0, 0));
  goalR = sz.y / 2; goalW = Math.max(sz.x, sz.z) / 2; goalCenter = b.getCenter(V3(0, 0, 0));
  if (snap) { fitR = goalR; fitW = goalW; fitCenter.copy(goalCenter); }
}

function clusterLayout(n, spacing) {
  if (n === 1) return [[0, 0]];
  if (n === 2) return [[-spacing / 2, 0], [spacing / 2, 0]];
  const ring = (m, R, off = 0) => Array.from({ length: m }, (_, i) => { const a = off + i / m * Math.PI * 2; return [R * Math.cos(a), R * Math.sin(a)]; });
  const ringR = m => Math.max(spacing / (2 * Math.sin(Math.PI / m)), spacing);
  if (n === 3 || n === 4) return ring(n, spacing / (2 * Math.sin(Math.PI / n)), n === 4 ? Math.PI / 4 : Math.PI / 2);
  const m = n - 1;
  return [[0, 0], ...ring(m, ringR(m), Math.PI / m)];
}

function disposeGroup(g) {
  const geos = new Set();
  g.traverse(o => { if (o.geometry && o.geometry !== plumeGeo) geos.add(o.geometry); });
  geos.forEach(x => x.dispose());
}

function assemble(ev, n, opt = {}) {
  const out = new THREE.Group();
  const base = buildEngine(ev, opt);
  const C = base.userData.C;
  const spacing = Math.max(2 * ev.Re + 0.18 * ev.Re, 2 * base.userData.Rm) * 1.04;
  const pos = clusterLayout(n, spacing);
  const roots = [];
  const pl = [], ggs = [];
  pos.forEach(([x, z], i) => {
    const r = i === 0 ? base : base.clone();
    r.position.set(x, 0, z);
    if (n > 1) r.rotation.y = Math.atan2(z, x) || 0;
    out.add(r); roots.push(r);
    // plumes, parented to the engine root at the exit plane
    for (const kind of ['outer', 'core']) {
      const m = new THREE.Mesh(plumeGeo, plumeMaterial(PLUME_COLORS[ev.d.prop]));
      m.position.y = base.userData.exitY; m.frustumCulled = false; m.renderOrder = 5; m.userData.noPick = true;
      r.add(m); pl.push({ mesh: m, kind });
    }
    if (base.userData.gg) {
      const gp = base.userData.gg;
      const m = new THREE.Mesh(plumeGeo, plumeMaterial(PLUME_COLORS.GG));
      m.position.copy(gp.pos); m.quaternion.setFromUnitVectors(V3(0, -1, 0), gp.dir.clone().normalize());
      m.frustumCulled = false; m.renderOrder = 5; m.userData.noPick = true;
      const u = m.material.uniforms;
      u.uR0.value = gp.r; u.uFar.value = 1.4; u.uSpread.value = Math.tan(5 * Math.PI / 180); u.uLen.value = gp.r * 16; u.uFade.value = 2.2; u.uDia.value = 0; u.uLam.value = 1;
      r.add(m); ggs.push(m);
    }
  });
  if (n > 1) {
    const ext = pos.reduce((mx, [x, z]) => Math.max(mx, Math.hypot(x, z)), 0) + base.userData.Rm * 1.15;
    const pg = new THREE.Group(); pg.userData.explode = [0, 1.4 * base.userData.Rc, 0]; pg.userData.gname = 'plate';
    const th = 0.12 * base.userData.Rc + 0.02 * ext;
    const p = new THREE.Mesh(new THREE.CylinderGeometry(ext, ext * 0.98, th, 72), M.plate);
    p.position.y = base.userData.yTop + th / 2 + 0.02 * base.userData.Rc; p.userData.key = 'plate'; pg.add(p);
    const rimT = torus(ext, th * 0.35, p.position.y + th / 2, M.dark, 96); rimT.userData.key = 'plate'; pg.add(rimT);
    out.add(pg); out.userData.plate = pg;
  }
  out.userData.base = base;
  return { out, roots, pl, ggs };
}

let current = null;
function rebuild() {
  if (display) { scene.remove(display); disposeGroup(display); display.traverse(o => { if (o.material && o.material.isShaderMaterial) o.material.dispose(); }); }
  const a = assemble(ev, state.cluster);
  display = a.out; engines = a.roots; plumes = a.pl; ggPlumes = a.ggs;
  scene.add(display);
  // fit
  const box = new THREE.Box3();
  display.updateMatrixWorld(true);
  display.traverse(o => { if (o.isMesh && !o.userData.noPick) { o.geometry.computeBoundingBox(); const b = o.geometry.boundingBox.clone().applyMatrix4(o.matrixWorld); box.union(b); } });
  engineBox = box.clone();
  refit(true);
  current = a;
  buildLabels();
  applyCut();
  updatePlumes(true);
}

/* ───────────────────────── Orbit controls ───────────────────────── */
const orbit = { az: 0.72, pol: 1.28, zoom: 1, drag: null, pointers: new Map(), pinch0: 0 };
function placeCamera() {
  const tf = Math.tan(camera.fov * Math.PI / 360);
  const dist = (Math.max(fitR / tf, fitW / (tf * camera.aspect)) + fitW) * 1.04 * orbit.zoom;
  const t = fitCenter.clone();
  camera.position.set(t.x + dist * Math.sin(orbit.pol) * Math.cos(orbit.az), t.y + dist * Math.cos(orbit.pol), t.z + dist * Math.sin(orbit.pol) * Math.sin(orbit.az));
  camera.near = dist / 200; camera.far = dist * 20; camera.updateProjectionMatrix();
  camera.lookAt(t);
}
view.addEventListener('pointerdown', e => {
  view.setPointerCapture(e.pointerId);
  orbit.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  if (orbit.pointers.size === 2) { const [p, q] = [...orbit.pointers.values()]; orbit.pinch0 = Math.hypot(p.x - q.x, p.y - q.y); orbit.zoom0 = orbit.zoom; }
  orbit.drag = { x: e.clientX, y: e.clientY, moved: false };
  $('tip').hidden = true;
});
view.addEventListener('pointermove', e => {
  if (orbit.pointers.has(e.pointerId)) orbit.pointers.set(e.pointerId, { x: e.clientX, y: e.clientY });
  if (orbit.pointers.size === 2) {
    const [p, q] = [...orbit.pointers.values()];
    const d = Math.hypot(p.x - q.x, p.y - q.y);
    orbit.zoom = Math.min(3, Math.max(0.35, orbit.zoom0 * orbit.pinch0 / Math.max(d, 1)));
    return;
  }
  if (orbit.drag && orbit.pointers.size === 1) {
    const dx = e.clientX - orbit.drag.x, dy = e.clientY - orbit.drag.y;
    if (Math.abs(dx) + Math.abs(dy) > 2) orbit.drag.moved = true;
    orbit.az += dx * 0.008; orbit.pol = Math.min(Math.PI - 0.12, Math.max(0.12, orbit.pol - dy * 0.006));
    orbit.drag.x = e.clientX; orbit.drag.y = e.clientY;
  } else {
    hover(e);
  }
});
const endPtr = e => { orbit.pointers.delete(e.pointerId); if (orbit.pointers.size === 0) orbit.drag = null; };
view.addEventListener('pointerup', endPtr);
view.addEventListener('pointercancel', endPtr);
view.addEventListener('pointerleave', () => { $('tip').hidden = true; });
view.addEventListener('wheel', e => { e.preventDefault(); orbit.zoom = Math.min(3, Math.max(0.35, orbit.zoom * Math.exp(e.deltaY * 0.0012))); }, { passive: false });
view.addEventListener('dblclick', () => { orbit.zoom = 1; orbit.pol = 1.28; });

/* ───────────────────────── Hover tooltips ───────────────────────── */
const ray = new THREE.Raycaster();
const ndc = new THREE.Vector2();
function hover(e) {
  if (!display) return;
  const rect = view.getBoundingClientRect();
  ndc.set(((e.clientX - rect.left) / rect.width) * 2 - 1, -((e.clientY - rect.top) / rect.height) * 2 + 1);
  ray.setFromCamera(ndc, camera);
  const hits = ray.intersectObject(display, true).filter(h => h.object.userData.key && !h.object.userData.noPick && h.object.visible && isVisible(h.object) && !(state.cut && clipped(h.point)));
  const tip = $('tip');
  if (!hits.length) { tip.hidden = true; return; }
  const k = hits[0].object.userData.key;
  const inf = INFO[k] || INFO[k === 'throat' ? 'chamber' : 'chamber'];
  const extra = tipExtra(k);
  tip.innerHTML = `<b>${inf[0]}</b>${inf[1]}${extra ? `<span class="k">${extra}</span>` : ''}`;
  tip.hidden = false;
  const x = e.clientX - rect.left, y = e.clientY - rect.top;
  const w = tip.offsetWidth, h = tip.offsetHeight;
  tip.style.left = Math.min(rect.width - w - 8, x + 14) + 'px';
  tip.style.top = Math.min(rect.height - h - 8, Math.max(8, y + 14)) + 'px';
}
function isVisible(o) { while (o) { if (!o.visible) return false; o = o.parent; } return true; }
function clipped(p) { return cutPlane.distanceToPoint(p) < 0; }

/* ───────────────────────── Labels ───────────────────────── */
const LABEL_TEXT = { mount: 'Mount', gimbal: 'Gimbal', actuator: 'Actuators', injector: 'Injector dome', chamber: 'Chamber', throat: 'Throat', bell: 'Regen bell', ext: 'Radiative extension',
  manifold: 'Regen manifold', feed: 'Feed lines', turbopump: 'Turbopump', gg: 'Gas generator', exhaust: 'Turbine exhaust', preburner: 'Preburner', hotgas: 'Hot-gas duct',
  pump: 'Electric pump', motor: 'Motor', inverter: 'Inverter + battery', bottle: 'Helium bottle', valve: 'Main valve', reactor: 'Reactor vessel', shield: 'Shield', drums: 'Control drums' };
let labelEls = [];
function buildLabels() {
  const host = $('labels'); host.innerHTML = ''; labelEls = [];
  const base = display.userData.base;
  for (const a of base.userData.anchors) {
    const el = document.createElement('div'); el.className = 'lbl';
    el.innerHTML = `<i></i><span>${LABEL_TEXT[a.key] || a.key}</span>`;
    host.appendChild(el);
    labelEls.push({ el, a });
  }
}
const _v = new THREE.Vector3(), _n = new THREE.Vector3(), _c = new THREE.Vector3();
function updateLabels() {
  const show = state.labels && !state.cut && state.cluster === 1;
  const rect = view.getBoundingClientRect();
  const base = display.userData.base;
  base.updateMatrixWorld(true);
  camera.getWorldPosition(_c);
  const placed = [];
  for (const { el, a } of labelEls) {
    const g = base.userData.groups[a.gname];
    if (!show || !g || !g.visible) { el.style.opacity = 0; continue; }
    if (a.sym) {
      const lc = base.worldToLocal(_c.clone());
      const phi = Math.atan2(lc.z, lc.x) + (a.key === 'mount' || a.key === 'gimbal' ? -0.6 : 0.6);
      a.p.set(a.sym.r * Math.cos(phi), a.sym.y, a.sym.r * Math.sin(phi));
      a.n.set(Math.cos(phi), 0, Math.sin(phi));
    }
    _v.copy(a.p).add(g.position).applyMatrix4(base.matrixWorld);
    _n.copy(a.n).transformDirection(base.matrixWorld);
    const toCam = _c.clone().sub(_v).normalize();
    const facing = _n.dot(toCam);
    const p = _v.clone().project(camera);
    const x = (p.x * 0.5 + 0.5) * rect.width, y = (-p.y * 0.5 + 0.5) * rect.height;
    const vis = facing > -0.05 && p.z < 1 && x > 10 && x < rect.width - 10 && y > 50 && y < rect.height - 10;
    el.style.opacity = vis ? 1 : 0;
    const right = x > rect.width * 0.5;
    placed.push({ el, x, y, right, vis });
  }
  for (const side of [true, false]) {
    const L = placed.filter(q => q.vis && q.right === side).sort((p, q) => p.y - q.y);
    let last = -1e9;
    for (const q of L) { q.ly = Math.max(q.y, last + 21); last = q.ly; }
  }
  for (const q of placed) {
    const ly = q.ly ?? q.y;
    q.el.classList.toggle('left', !q.right);
    const w = q.el.offsetWidth;
    q.el.style.transform = `translate(${q.right ? q.x - 4 : q.x - w + 4}px, ${ly - 9}px)`;
  }
}

/* ───────────────────────── Cutaway, explode, plumes ───────────────────────── */
function applyCut() {
  for (const m of CLIPPABLE) { m.clippingPlanes = state.cut ? [cutPlane] : null; m.needsUpdate = true; }
  if (!display) return;
  display.traverse(o => {
    if (o.userData.gname && /^kit|plumbing|drums/.test(o.userData.gname)) o.visible = !state.cut && !(state.exploded && o.userData.gname === 'plumbing' && explodeT > 0.05);
  });
}
function updatePlumes(force) {
  if (!ev) return;
  const on = state.plume && explodeT < 0.02;
  const pOut = plumeParams(ev, state.alt, 'outer');
  const pCore = plumeParams(ev, state.alt, 'core');
  for (const { mesh, kind } of plumes) {
    mesh.visible = on;
    const p = kind === 'core' ? pCore : pOut;
    const u = mesh.material.uniforms;
    u.uR0.value = p.r0; u.uFar.value = p.far; u.uSpread.value = p.spread; u.uLen.value = p.len; u.uLam.value = p.lam; u.uDia.value = p.dia; u.uFade.value = p.fade; u.uInt.value = p.inten;
  }
  const ggIntensity = state.alt > 60 ? 0.45 : 0.9;
  for (const m of ggPlumes) { m.visible = on && !state.cut; m.material.uniforms.uInt.value = ggIntensity; m.material.uniforms.uSpread.value = Math.tan((state.alt > 60 ? 18 : 5) * Math.PI / 180); }
  const glow = on ? 0.4 : 0;
  M.niobium.emissiveIntensity = glow; M.cc.emissiveIntensity = glow * 1.1;
}

/* ───────────────────────── Render loop ───────────────────────── */
let lastT = performance.now(), dirty = true, lastBuild = 0;
function resize() {
  const r = view.getBoundingClientRect();
  renderer.setSize(r.width, r.height, false);
  camera.aspect = r.width / Math.max(1, r.height);
  camera.updateProjectionMatrix();
}
new ResizeObserver(resize).observe(view);
const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
if (reduceMotion) { state.spin = false; }
if (window.innerWidth < 640) { state.labels = false; }

function frame(t) {
  const dt = Math.min(0.05, (t - lastT) / 1000); lastT = t;
  if (dirty && t - lastBuild > 90) { dirty = false; lastBuild = t; rebuild(); }
  if (state.spin && !orbit.drag) orbit.az += dt * 0.22;
  // explode animation
  const target = state.exploded ? 1 : 0;
  if (Math.abs(explodeT - target) > 1e-3) {
    explodeT += (target - explodeT) * Math.min(1, dt * 6);
    if (Math.abs(explodeT - target) < 1e-3) explodeT = target;
    const e = explodeT * explodeT * (3 - 2 * explodeT);
    for (const r of engines) r.children.forEach(g => { const ex = g.userData.explode; if (ex) g.position.set(ex[0] * e, ex[1] * e, ex[2] * e); });
    if (display.userData.plate) { const ex = display.userData.plate.userData.explode; display.userData.plate.position.set(0, ex[1] * e, 0); }
    applyCut(); updatePlumes();
  }
  fitR += (goalR - fitR) * Math.min(1, dt * 5); fitW += (goalW - fitW) * Math.min(1, dt * 5); fitCenter.lerp(goalCenter, Math.min(1, dt * 5));
  placeCamera();
  // cut plane faces the camera
  const cd = V3(camera.position.x - fitCenter.x, 0, camera.position.z - fitCenter.z).normalize();
  cutPlane.normal.set(-cd.x, 0, -cd.z); cutPlane.constant = cd.x * fitCenter.x + cd.z * fitCenter.z;
  const time = t / 1000;
  for (const { mesh } of plumes) mesh.material.uniforms.uTime.value = time;
  for (const m of ggPlumes) m.material.uniforms.uTime.value = time;
  renderer.render(scene, camera);
  if (display) updateLabels();
  requestAnimationFrame(frame);
}

/* ───────────────────────── Controls UI ───────────────────────── */
const tierLocked = (t, need) => t >= 0 && t < need;
const tierName = n => n < 0 ? 'Sandbox' : `Tier ${n}`;

function buildControls() {
  const ps = $('preset');
  ps.innerHTML = '<option value="">Custom design</option>' + Object.entries(PRESETS).map(([k, p]) => `<option value="${k}">${p.name}</option>`).join('');
  ps.value = 'merlin';
  ps.onchange = () => { if (ps.value) loadPreset(ps.value); };
  const ts = $('tier');
  ts.innerHTML = '<option value="-1">Sandbox (no caps)</option>' + TIERS.map(t => `<option value="${t.n}">Tier ${t.n} · ${t.nodes}</option>`).join('');
  ts.onchange = () => { state.tier = +ts.value; lastEdit = 'tier'; enforce(); update(); };
  $('fuel').innerHTML = Object.entries(FUELS).map(([k, f]) => `<option value="${k}">${f.label} · Tc ${f.tc} K</option>`).join('');
  $('fuel').onchange = () => { state.fuel = $('fuel').value; lastEdit = 'fuel'; markCustom(); update(); };
  $('props').innerHTML = PROP_ORDER.map(k => `<button type="button" data-p="${k}"><b><span class="dot" style="background:${PROPS[k].color}"></span>${PROPS[k].label}</b><small>${PROPS[k].sub}</small></button>`).join('');
  $('props').onclick = e => {
    const b = e.target.closest('button'); if (!b || b.disabled) return;
    const p = b.dataset.p; state.prop = p; lastEdit = 'prop';
    if (!CYCLES[state.cycle].props.includes(p)) state.cycle = CYCLE_ORDER.find(c => CYCLES[c].props.includes(p) && !tierLocked(state.tier, CYCLES[c].tier)) || CYCLE_ORDER.find(c => CYCLES[c].props.includes(p));
    markCustom(); enforce(); update();
  };
  $('cycles').innerHTML = CYCLE_ORDER.map(k => `<button type="button" data-c="${k}"><span class="abbr">${CYCLES[k].short}</span><span class="nm">${CYCLES[k].label}</span><span class="lock"></span></button>`).join('');
  $('cycles').onclick = e => {
    const b = e.target.closest('button'); if (!b || b.disabled) return;
    const c = b.dataset.c; state.cycle = c; lastEdit = 'cycle';
    if (!CYCLES[c].props.includes(state.prop)) state.prop = CYCLES[c].props.find(p => !tierLocked(state.tier, PROPS[p].tier)) || CYCLES[c].props[0];
    if (c === 'Nuclear' && state.eps < 60) state.eps = 100;
    markCustom(); enforce(); update();
  };
  // sliders
  $('pc').oninput = () => { state.pc = +$('pc').value; lastEdit = 'pc'; markCustom(); update(); };
  $('eps').oninput = () => { state.eps = fromLog($('eps').value, 2, epsMax()); lastEdit = 'eps'; markCustom(); update(); };
  $('thr').oninput = () => { state.thrust = fromLog($('thr').value, 1, thrMax()); lastEdit = 'thrust'; markCustom(); update(); };
  $('lf').oninput = () => { state.lf = +$('lf').value; lastEdit = 'lf'; markCustom(); update(); };
  $('alt').oninput = () => { state.alt = +$('alt').value; lastEdit = 'alt'; updateAmbient(); };
  document.querySelectorAll('[data-alt]').forEach(b => b.onclick = () => { state.alt = +b.dataset.alt; $('alt').value = state.alt; lastEdit = 'alt'; updateAmbient(); });
  const tog = (id, key, fn) => { const b = $(id); b.onclick = () => { state[key] = !state[key]; b.setAttribute('aria-pressed', state[key]); if (fn) fn(); }; b.setAttribute('aria-pressed', state[key]); };
  tog('tPlume', 'plume', () => { updatePlumes(); refit(); });
  tog('tExplode', 'exploded', () => { lastEdit = 'explode'; renderWhy(); refit(); });
  tog('tCut', 'cut', () => { applyCut(); updatePlumes(); lastEdit = 'cut'; renderWhy(); });
  tog('tLabels', 'labels');
  tog('tSpin', 'spin');
  $('cluster').innerHTML = [1, 2, 3, 4, 5, 7, 9].map(n => `<button class="tbtn" data-n="${n}" aria-pressed="false">${n}</button>`).join('');
  $('cluster').onclick = e => { const b = e.target.closest('button'); if (!b) return; state.cluster = +b.dataset.n; lastEdit = 'cluster'; markCustom(); update(); };
}
function markCustom() { $('preset').value = ''; }
const toLog = (v, lo, hi) => Math.round(1000 * Math.log(v / lo) / Math.log(hi / lo));
const fromLog = (s, lo, hi) => { const v = lo * Math.pow(hi / lo, s / 1000); return v < 10 ? Math.round(v * 10) / 10 : v < 100 ? Math.round(v) : Math.round(v / 5) * 5; };
function caps() { return PE.tierCaps(state.tier); }
function pcRange() { const c = CYCLES[state.cycle].pc; return [c[0], Math.min(c[1], caps().pcMax)]; }
function epsMax() { return Math.min(300, caps().epsMax); }
function thrMax() {
  let m = caps().thrMax;
  if (state.cycle === 'Expander') m = Math.min(m, state.prop === 'Hydrolox' ? 300 : 100);
  if (state.cycle === 'Electric') m = Math.min(m, 400);
  if (state.cycle === 'PressureFed') m = Math.min(m, 600);
  return m;
}
function enforce() {
  const [lo, hi] = pcRange();
  state.pc = Math.min(hi, Math.max(lo, state.pc));
  state.eps = Math.min(epsMax(), Math.max(2, state.eps));
  state.thrust = Math.min(thrMax(), Math.max(1, state.thrust));
  if (state.cycle === 'Nuclear' && tierLocked(state.tier, FUELS[state.fuel].tier)) state.fuel = 'Graphite';
}
function loadPreset(k, scroll) {
  const p = PRESETS[k];
  Object.assign(state, { prop: p.prop, cycle: p.cycle, pc: p.pc, eps: p.eps, thrust: p.thrust, lf: p.lf, alt: p.alt, cluster: p.cluster, tier: -1, fuel: p.fuel || state.fuel });
  $('preset').value = k; $('tier').value = '-1'; $('alt').value = p.alt;
  lastEdit = 'preset:' + k;
  enforce(); update();
  if (scroll) $('designer').scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
}

function syncControls() {
  const t = state.tier, cp = caps();
  $('tierVal').textContent = tierName(t);
  $('tierHint').textContent = t < 0 ? 'Sandbox ignores tech caps.' : `Caps: Pc ≤ ${cp.pcMax} bar · ε ≤ ${cp.epsMax} · η_c* ${cp.etaC}${cp.cone ? ' · 15° cone nozzles only' : ''}${cp.ext ? ` · ${cp.ext} extensions` : ''}`;
  document.querySelectorAll('#props button').forEach(b => {
    const p = PROPS[b.dataset.p];
    const locked = tierLocked(t, p.tier);
    b.disabled = locked;
    b.title = locked ? `Unlocks at ${p.node} (Tier ${p.tier})` : '';
    b.setAttribute('aria-pressed', b.dataset.p === state.prop);
    b.querySelector('small').textContent = locked ? `Locked · ${p.node}` : p.sub;
  });
  document.querySelectorAll('#cycles button').forEach(b => {
    const c = CYCLES[b.dataset.c];
    const locked = tierLocked(t, c.tier);
    const compat = c.props.includes(state.prop);
    b.disabled = locked;
    b.setAttribute('aria-pressed', b.dataset.c === state.cycle);
    b.querySelector('.lock').textContent = locked ? `T${c.tier}` : (!compat ? `→ ${c.props.map(p => PROPS[p].label).join('/')}`.slice(0, 18) : `${c.pc[0]}–${c.pc[1]}`);
    b.title = locked ? `Unlocks at ${c.node} (Tier ${c.tier})` : (!compat ? `Switches propellant to ${PROPS[c.props[0]].label}` : `Pc ${c.pc[0]}–${c.pc[1]} bar`);
  });
  $('fuelGroup').hidden = state.cycle !== 'Nuclear';
  [...$('fuel').options].forEach(o => { const f = FUELS[o.value]; o.disabled = tierLocked(t, f.tier); });
  $('fuel').value = state.fuel;
  const [lo, hi] = pcRange();
  const pc = $('pc'); pc.min = lo; pc.max = hi; pc.value = state.pc;
  $('pcVal').textContent = `${fmt(state.pc, state.pc < 20 ? 1 : 0)} bar`; $('pcMin').textContent = lo; $('pcMax').textContent = `${hi} bar`;
  const em = epsMax();
  $('eps').value = toLog(state.eps, 2, em); $('epsVal').textContent = fmt(state.eps, state.eps < 10 ? 1 : 0); $('epsMin').textContent = '2'; $('epsMax').textContent = em;
  const tm = thrMax();
  $('thr').value = toLog(state.thrust, 1, tm); $('thrVal').textContent = `${fmt(state.thrust, state.thrust < 10 ? 1 : 0)} kN`; $('thrMin').textContent = '1 kN'; $('thrMax').textContent = `${fmt(tm, 0)} kN`;
  const lf = $('lf'); lf.value = state.lf; lf.disabled = cp.cone; $('lfVal').textContent = cp.cone ? '15° cone' : state.lf.toFixed(2);
  document.querySelectorAll('#cluster button').forEach(b => b.setAttribute('aria-pressed', +b.dataset.n === state.cluster));
}

function update() {
  ev = PE.evaluate({ prop: state.prop, cycle: state.cycle, pc: state.pc, eps: state.eps, thrust: state.thrust, lf: state.lf, tier: state.tier, fuel: state.fuel, cluster: state.cluster });
  syncControls();
  renderReadout();
  renderChart();
  renderProfile();
  renderKit();
  renderWhy();
  updateAmbient(true);
  dirty = true;
}

function updateAmbient(skipWhy) {
  const pa = PE.earthPressure(state.alt);
  const vac = state.alt >= 119.5;
  $('altVal').textContent = vac ? 'vacuum · 0 atm' : `${fmt(state.alt, state.alt < 10 ? 1 : 0)} km · ${pa / PE.ATM >= 0.01 ? fmt(pa / PE.ATM, 3) : (pa / PE.ATM).toExponential(1)} atm`;
  const [k, txt] = regimeOf(ev, state.alt);
  const rg = $('regime'); rg.dataset.k = k; rg.textContent = txt;
  updatePlumes();
  refit();
  renderChartMarker();
  if (!skipWhy) renderWhy();
}

function renderReadout() {
  const N = state.cluster;
  const sl = ev.sepSL;
  $('oIspV').innerHTML = `${fmt(ev.ispVac, 0)}<small>s</small>`;
  $('oIspS').innerHTML = `SL ${fmt(ev.ispSL, 0)} s${sl ? ' · separated' : ''}`;
  const Fv = ev.thrustVac / 1000 * N, Fs = ev.thrustSL / 1000 * N;
  const big = v => v >= 10000 ? `${fmt(v / 1000, 1)}<small>MN</small>` : `${fmt(v, v < 10 ? 1 : 0)}<small>kN</small>`;
  $('oFV').innerHTML = big(Fv);
  $('oFS').textContent = `SL ${Fs >= 10000 ? fmt(Fs / 1000, 2) + ' MN' : fmt(Fs, Fs < 10 ? 1 : 0) + ' kN'}${N > 1 ? ` · ${N} engines` : ''}`;
  const m = ev.totalCluster;
  $('oM').innerHTML = m >= 1000 ? `${fmt(m / 1000, 2)}<small>t</small>` : `${fmt(m, 0)}<small>kg</small>`;
  $('oMs').textContent = N > 1 ? `${fmt(ev.total, 0)} kg each + plate` : `${fmt(ev.total, 0)} kg engine`;
  $('oTWR').innerHTML = `${fmt(ev.twrVac, ev.twrVac < 10 ? 1 : 0)}`;
  $('oTWRs').textContent = `SL ${fmt(ev.twrSL, ev.twrSL < 10 ? 1 : 0)}`;
  const rows = [
    ['Mass flow', `${fmt(ev.mdot * N, ev.mdot < 10 ? 2 : 1)} kg/s`],
    ['Exit diameter', `${fmt(ev.De, 2)} m`],
    ['Length', `${fmt(ev.length, 2)} m`],
    ['Throat diameter', `${fmt(2 * ev.Rt * 100, 1)} cm`],
    ['Exit pressure', `${fmt(ev.pe / 1e5, ev.pe < 1e4 ? 3 : 2)} bar`],
    ['c* · Cf vac', `${fmt(ev.cstar, 0)} m/s · ${fmt(ev.cfv, 3)}`],
    ['η c* · η Cf', `${fmt(ev.etaC, 3)} · ${fmt(ev.etaCf, 3)}`],
  ];
  if (ev.nuclear) rows.push(['Reactor power', `${fmt(ev.Pth, 0)} MW`]);
  if (ev.d.cycle === 'Electric') rows.push(['EC draw', `${fmt(ev.ecDraw, 0)} EC/s`]);
  if (ev.PpumpKW > 0) rows.push(['Pump power', ev.PpumpKW >= 1000 ? `${fmt(ev.PpumpKW / 1000, 2)} MW` : `${fmt(ev.PpumpKW, 0)} kW`]);
  rows.push(['Separates below', ev.sepSL ? `${fmt(ev.sepAltKm, 1)} km` : 'never (SL ok)']);
  rows.push(['Min throttle', `${ev.cyc.minThr} %`]);
  rows.push(['Ignitions', ev.cyc.ign]);
  $('oKV').innerHTML = rows.map(([k, v]) => `<dt>${k}</dt><dd>${v}</dd>`).join('');
  $('hudTitle').textContent = `${PROPS[state.prop].label} ${CYCLES[state.cycle].label.toLowerCase()}${N > 1 ? ` × ${N}` : ''}`;
  $('hudSub').textContent = `Pc ${fmt(state.pc, 0)} bar · ε ${fmt(state.eps, 0)} · ${fmt(state.thrust, 0)} kN · Ø ${fmt(ev.De, 2)} m`;
  const w = $('warns');
  w.innerHTML = ev.warn.map(x => `<li data-l="${x.lvl}">${x.text}</li>`).join('');
}

/* Isp vs altitude chart */
const CH = { w: 300, h: 170, l: 34, r: 8, t: 10, b: 24, maxKm: 80 };
function chartX(km) { return CH.l + (CH.w - CH.l - CH.r) * Math.min(km, CH.maxKm) / CH.maxKm; }
let chartY = () => 0;
function renderChart() {
  const svg = $('chart');
  const pts = [];
  let sepTo = 0;
  for (let km = 0; km <= CH.maxKm; km += 0.5) {
    const r = ev.ispAt(PE.earthPressure(km));
    pts.push([km, r.isp]);
    if (r.sep) sepTo = km;
  }
  const iMax = ev.ispVac;
  const iMin0 = Math.min(...pts.map(p => p[1]));
  const span = iMax - iMin0;
  const step = span > 400 ? 200 : span > 160 ? 100 : span > 60 ? 50 : 20;
  const lo = Math.max(0, Math.floor((iMin0 - span * 0.08) / step) * step);
  const hi = Math.ceil((iMax + span * 0.06 + 1) / step) * step;
  chartY = v => CH.t + (CH.h - CH.t - CH.b) * (1 - (v - lo) / (hi - lo));
  let s = '';
  for (let v = lo; v <= hi + 1e-6; v += step) {
    const y = chartY(v);
    s += `<line x1="${CH.l}" x2="${CH.w - CH.r}" y1="${y}" y2="${y}" stroke="var(--line)" stroke-width="1"/>`;
    s += `<text x="${CH.l - 5}" y="${y + 3}" text-anchor="end">${v}</text>`;
  }
  for (const km of [0, 20, 40, 60, 80]) s += `<text x="${chartX(km)}" y="${CH.h - 8}" text-anchor="${km === 0 ? 'start' : km === 80 ? 'end' : 'middle'}">${km}${km === 80 ? ' km' : ''}</text>`;
  if (sepTo > 0) {
    s += `<rect x="${CH.l}" y="${CH.t}" width="${chartX(sepTo) - CH.l}" height="${CH.h - CH.t - CH.b}" fill="var(--sep-fill)" opacity="0.14"/>`;
    s += `<text x="${CH.l + 5}" y="${CH.t + 12}" style="fill:var(--bad)">separated</text>`;
  }
  s += `<line x1="${CH.l}" x2="${CH.w - CH.r}" y1="${chartY(iMax)}" y2="${chartY(iMax)}" stroke="var(--muted)" stroke-dasharray="3 3" stroke-width="1"/>`;
  const d = pts.map((p, i) => `${i ? 'L' : 'M'}${chartX(p[0]).toFixed(1)},${chartY(p[1]).toFixed(1)}`).join('');
  s += `<path d="${d} L${chartX(CH.maxKm)},${CH.h - CH.b} L${CH.l},${CH.h - CH.b} Z" fill="var(--copper)" opacity="0.10"/>`;
  s += `<path d="${d}" fill="none" stroke="var(--copper)" stroke-width="2" stroke-linejoin="round"/>`;
  s += `<g id="chartMark"></g>`;
  svg.innerHTML = s;
  $('chartNote').textContent = `vac ${fmt(iMax, 0)} s`;
  renderChartMarker();
}
function renderChartMarker() {
  const g = document.getElementById('chartMark'); if (!g || !ev) return;
  const km = Math.min(state.alt, CH.maxKm);
  const isp = state.alt >= 119.5 ? ev.ispVac : ev.ispAt(PE.earthPressure(state.alt)).isp;
  const x = chartX(km), y = chartY(isp);
  g.innerHTML = `<line x1="${x}" x2="${x}" y1="${CH.t}" y2="${CH.h - CH.b}" stroke="var(--ink)" stroke-width="1" opacity=".35"/>` +
    `<circle cx="${x}" cy="${y}" r="4" fill="var(--surface)" stroke="var(--ink)" stroke-width="2"/>` +
    `<text x="${x > CH.w - 70 ? x - 7 : x + 7}" y="${Math.max(CH.t + 10, y - 7)}" text-anchor="${x > CH.w - 70 ? 'end' : 'start'}" style="fill:var(--ink);font-weight:600">${fmt(isp, 0)} s${state.alt > CH.maxKm ? ' →' : ''}</text>`;
}

/* Profile half-section SVG (§8.2) */
function renderProfile() {
  const C = PE.contour(ev);
  const svg = $('profile');
  const W = 1000, H = 440, pad = 56;
  const domeH = 0.42 * (C.Rc * 1.05);
  const x0 = C.xInj - domeH, x1 = C.E.x;
  const rMax = Math.max(C.Re, C.Rc * 1.05);
  const sc = Math.min((W - 2 * pad - 130) / (x1 - x0), (H / 2 - 40) / rMax);
  const cy = H / 2;
  const ox = pad + 40 - x0 * sc;
  const X = x => ox + x * sc, Yt = r => cy - r * sc, Yb = r => cy + r * sc;
  const path = (pts, Y) => pts.map((p, i) => `${i ? 'L' : 'M'}${X(p.x).toFixed(1)},${Y(p.r).toFixed(1)}`).join('');
  const sec = [
    ['chamber', [...C.pts.chamber, ...C.pts.conv], 'var(--muted)'],
    ['throat', C.pts.throat, 'var(--ink)'],
    ['bell', C.pts.bell, 'var(--copper)'],
    ['ext', C.pts.ext, 'var(--ink)'],
  ];
  let s = '';
  s += `<line x1="${X(x0) - 20}" x2="${X(x1) + 30}" y1="${cy}" y2="${cy}" stroke="var(--muted)" stroke-dasharray="10 4 2 4" stroke-width="1"/>`;
  // mirrored fill (lower half)
  const allP = [...C.pts.chamber, ...C.pts.conv, ...C.pts.throat, ...C.pts.bell, ...C.pts.ext];
  s += `<path d="${path(allP, Yb)} L${X(x1)},${cy} L${X(C.xInj)},${cy} Z" fill="var(--surface-2)" opacity=".8"/>`;
  s += `<path d="${path(allP, Yb)}" fill="none" stroke="var(--muted)" stroke-width="1.5"/>`;
  // dome
  const Rd = C.Rc * 1.05;
  s += `<path d="M${X(C.xInj)},${Yt(Rd)} Q${X(C.xInj - domeH * 1.15)},${Yt(Rd * 0.98)} ${X(C.xInj - domeH)},${cy}" fill="none" stroke="var(--muted)" stroke-width="2"/>`;
  s += `<path d="M${X(C.xInj)},${Yb(Rd)} Q${X(C.xInj - domeH * 1.15)},${Yb(Rd * 0.98)} ${X(C.xInj - domeH)},${cy}" fill="none" stroke="var(--muted)" stroke-width="1.5" opacity=".7"/>`;
  s += `<line x1="${X(C.xInj)}" x2="${X(C.xInj)}" y1="${Yt(C.Rc)}" y2="${Yb(C.Rc)}" stroke="var(--muted)" stroke-width="1.5" stroke-dasharray="3 3"/>`;
  for (const [, pts, col] of sec) if (pts.length > 1) s += `<path d="${path(pts, Yt)}" fill="none" stroke="${col}" stroke-width="${col === 'var(--copper)' ? 4 : 3}" stroke-linejoin="round" stroke-linecap="round"/>`;
  // Bézier construction: N, Q, E with tangents
  if (!ev.cone) {
    s += `<path d="M${X(C.N.x)},${Yt(C.N.r)} L${X(C.Q.x)},${Yt(C.Q.r)} L${X(C.E.x)},${Yt(C.E.r)}" fill="none" stroke="var(--muted)" stroke-dasharray="5 4" stroke-width="1.2"/>`;
  }
  const dotAt = (p, lab, dx, dy, anchor = 'start') => `<circle cx="${X(p.x)}" cy="${Yt(p.r)}" r="5" fill="var(--surface)" stroke="var(--ink)" stroke-width="2"/><text x="${X(p.x) + dx}" y="${Yt(p.r) + dy}" text-anchor="${anchor}">${lab}</text>`;
  s += dotAt(C.N, `N · θn ${fmt(C.thN, 1)}°`, 10, 22, 'start');
  if (!ev.cone) s += dotAt(C.Q, 'Q', 0, -12, 'middle');
  s += dotAt(C.E, `E · θe ${fmt(C.thE, 1)}°`, -10, -12, 'end');
  if (C.split) s += `<circle cx="${X(C.split.x)}" cy="${Yt(C.split.r)}" r="4" fill="var(--copper)"/><text x="${X(C.split.x) + 8}" y="${Yt(C.split.r) + 18}">ε_regen ${fmt(ev.regenEps, 0)} · ${ev.extMat} beyond</text>`;
  // Dimensions
  const dimV = (x, r, lab) => `<line x1="${X(x)}" x2="${X(x)}" y1="${cy}" y2="${Yb(r)}" stroke="var(--ink)" stroke-width="1"/><line x1="${X(x) - 5}" x2="${X(x) + 5}" y1="${Yb(r)}" y2="${Yb(r)}" stroke="var(--ink)"/><text x="${X(x) + 6}" y="${(cy + Yb(r)) / 2 + 4}">${lab}</text>`;
  s += dimV(0, C.Rt, `Rt ${fmt(C.Rt * 100, 1)} cm`);
  s += dimV(C.E.x, C.Re, `Re ${fmt(C.Re, 2)} m`);
  s += dimV((C.xInj + C.xc) / 2, C.Rc, `Rc ${fmt(C.Rc * 100, 1)} cm`);
  const yL = Math.min(H - 14, Yb(rMax) + 26);
  s += `<line x1="${X(0)}" x2="${X(C.E.x)}" y1="${yL}" y2="${yL}" stroke="var(--muted)" stroke-width="1"/><line x1="${X(0)}" x2="${X(0)}" y1="${yL - 5}" y2="${yL + 5}" stroke="var(--muted)"/><line x1="${X(C.E.x)}" x2="${X(C.E.x)}" y1="${yL - 5}" y2="${yL + 5}" stroke="var(--muted)"/>`;
  s += `<text x="${(X(0) + X(C.E.x)) / 2}" y="${yL - 6}" text-anchor="middle" class="m">Ln ${fmt(C.E.x, 2)} m${ev.cone ? ' (15° cone)' : ` = Lf·(√ε − 1)·Rt / tan 15°, Lf ${ev.d.lf.toFixed(2)}`}</text>`;
  // Callouts
  const tx = (x, y, t, a = 'start', cls = '') => `<text x="${x}" y="${y}" text-anchor="${a}" class="${cls}">${t}</text>`;
  s += tx(X((C.xInj + C.xc) / 2), Yt(C.Rc) - 14, ev.nuclear ? 'reactor core · L = 2.4·Rv' : `chamber · L* ${PROPS[state.prop].lstar} m`, 'middle');
  s += tx(X(C.A.x) - 4, Yt(C.A.r) - 30, '1.5·Rt arc', 'end', 'm');
  s += tx(X(0), Yb(C.Rt) + 20, 'throat · 0.382·Rt arc', 'middle', 'm');
  s += tx(X(C.xInj - domeH) - 6, cy - 8, 'injector', 'end', 'm');
  svg.innerHTML = s;
}

function renderKit() {
  const c = CYCLES[state.cycle];
  $('kitTitle').textContent = `Kit · ${c.label}`;
  const common = [['Chamber + bell', 'Revolved profile, 64 segments, two shells (metal outside, soot inside).'], ['Gimbal block + mount', 'Pivot at the throat plane, attach node on top.']];
  const items = c.kit.map(k => [k, KIT_DESC[k] || '']);
  $('kitList').innerHTML = [...items, ...common].map(([b, t], i) => `<li><span class="n">${String(i + 1).padStart(2, '0')}</span><div><b>${b}</b><span>${t}</span></div></li>`).join('');
}
const KIT_DESC = {
  'Helium bottles ×2': 'COPV spheres sized by ṁ × rated burn time × Pc.',
  'Main valves': 'Tank pressure pushes propellant straight through.',
  'No pumps': 'Pc is capped at 25 bar, so the engine is large for its thrust.',
  'Pumps ×2': 'One per propellant, each on its own motor.',
  'Motor cans ×2': 'Mass scales with shaft power.',
  'Inverter + battery box': 'Hangs under the mount; orange HV cables to the motors.',
  'Single-shaft turbopump': 'Ox and fuel pumps on one shaft, turbine between.',
  'Gas generator': 'Small combustor feeding the turbine.',
  'Turbine exhaust duct': 'Runs down the bell and ends in its own small nozzle.',
  'Small turbopump': 'Driven by hydrogen warmed in the cooling jacket.',
  'Ox pump': 'Geared or on a second shaft.',
  'Regen jacket manifolds': 'Torus at the end of the cooled section plus one on the chamber.',
  'Big turbopump': 'Pump pressure rise ≈ 2 × Pc.',
  'Preburner on top': 'Fuel-rich (hydrolox) or ox-rich (kerolox, methalox).',
  'Hot-gas duct to injector': 'All turbine gas ends up in the main chamber.',
  'Fuel-rich turbopump + preburner': 'Drives the fuel pump.',
  'Ox-rich turbopump + preburner': 'Drives the oxidiser pump, symmetric across the chamber.',
  'Two hot-gas ducts': 'Both gas streams enter the injector fully gasified.',
  'Reactor vessel': 'Replaces the chamber. Radius ∝ (P_th)^1/3.',
  'Shield disc': 'Between reactor and mount.',
  'Control-drum ring': '12 drums with absorber faces.',
  'One H₂ pump': 'Hydrogen cools the nozzle, then drives the turbine, then enters the core.',
};

/* Why? box */
function renderWhy() {
  const el = $('why'); if (!ev) return;
  const e = lastEdit;
  let t = '';
  const sep = ev.sepSL ? `the flow separates below ${fmt(ev.sepAltKm, 1)} km on Earth` : 'it stays attached at sea level';
  if (e.startsWith('preset')) t = `<b>${PRESETS[e.split(':')[1]]?.name || 'Preset'}.</b> Loaded in sandbox. ${CYCLES[state.cycle].massNote}.`;
  else if (e === 'pc') t = `<b>Chamber pressure.</b> Higher Pc shrinks the throat for the same thrust (At = F / (η·Cf·Pc)) and raises pump power. Here pe = ${fmt(ev.pe / 1e5, 2)} bar, so ${sep}.`;
  else if (e === 'eps') t = `<b>Expansion ratio.</b> Higher ε gives more vacuum Isp (${fmt(ev.ispVac, 0)} s now), but ${sep}.`;
  else if (e === 'thrust') t = `<b>Thrust</b> sets the throat area, and the whole mesh scales with it. Kit pieces grow with pump power^1/3, so small engines look pump-heavy.`;
  else if (e === 'lf') t = `<b>Bell length.</b> Lf ${state.lf.toFixed(2)}: a shorter bell is lighter and more compact but loses a little to divergence. θn ${fmt(ev.angles.thetaN, 1)}°, θe ${fmt(ev.angles.thetaE, 1)}°.`;
  else if (e === 'cycle') t = `<b>${CYCLES[state.cycle].label}.</b> ${CYCLES[state.cycle].massNote}. Isp effect: ${CYCLES[state.cycle].isp}.`;
  else if (e === 'prop') t = state.prop === 'Hydrolox' ? `<b>Hydrolox.</b> Highest chemical Isp, but LH₂ at 0.36 t/m³ needs about 3× the pump power per kg/s of kerolox, so TWR drops.` : state.prop === 'Methalox' ? `<b>Methalox.</b> Between kerolox and hydrolox in Isp, no coking, dense enough for compact pumps. The full-flow cycle is methalox only for now.` : state.prop === 'NTR-H2' ? `<b>NTR-H₂.</b> Pure hydrogen heated by a reactor. Isp near 900 s, but the reactor dominates the mass.` : `<b>Kerolox.</b> Dense and easy to pump, lowest Isp of the three. Ox-rich staged combustion needs the metallurgy node.`;
  else if (e === 'tier') t = state.tier < 0 ? '<b>Sandbox.</b> No caps.' : `<b>Tier ${state.tier}.</b> Bounds come from the researched GPTT nodes (${TIERS[state.tier].nodes}). Locked entries show the node that unlocks them.`;
  else if (e === 'cluster') t = `<b>Cluster × ${state.cluster}.</b> The mesh is built once and drawn ${state.cluster} times on a generated thrust plate: one part instead of ${state.cluster * 2}+.`;
  else if (e === 'alt') { const [, r] = regimeOf(ev, state.alt); t = `<b>${r}.</b> Ambient ${state.alt >= 119.5 ? '0' : fmt(PE.earthPressure(state.alt) / 1e5, 3)} bar against pe ${fmt(ev.pe / 1e5, 3)} bar. ${state.alt >= 119.5 ? 'In vacuum the plume opens wide and faint.' : ev.pe < PE.earthPressure(state.alt) ? 'The jet necks in and shows shock diamonds.' : 'The jet keeps expanding after the exit.'}`; }
  else if (e === 'explode') t = '<b>Exploded view.</b> Sections and kit pieces pulled apart along their placement axes. Plumbing hides while exploded.';
  else if (e === 'cut') t = '<b>Cutaway.</b> Designer-only toggle: hides the kit and clips the shells so you see the injector face, chamber and soot-dark inner wall.';
  else if (e === 'fuel') t = `<b>${FUELS[state.fuel].label}.</b> Tc ${FUELS[state.fuel].tc} K. Hotter fuel elements raise Isp and cut reactor mass per MW.`;
  el.innerHTML = t;
}

/* ───────────────────────── Cycle cards with generated thumbnails ───────────────────────── */
function buildCards() {
  const host = $('cards');
  const list = [...CYCLE_ORDER.map(k => ({ k, c: CYCLES[k], preset: CYCLE_PRESET[k] })), { k: 'Bleed', planned: true }];
  host.innerHTML = list.map(({ k, c, preset, planned }) => {
    if (planned) return `<article class="card planned"><div class="thumb"><img id="th-Bleed" alt="Bleed expander render"><span class="ex">LE-5B, LE-9</span></div><div class="body">
      <div class="top"><h3>Expander (bleed)</h3><span class="pc">30–90 bar</span></div>
      <dl class="kv"><dt>Isp</dt><dd>−1 %: some heated H₂ is dumped</dd><dt>Mass</dt><dd>Lifts the expander thrust cap about 3×</dd><dt>Ignitions</dt><dd>Good</dd><dt>Propellant</dt><dd>Hydrolox, Methalox</dd></dl>
      <div class="foot"><span class="unlock">Later phase</span><button class="load" disabled>Planned</button></div></div></article>`;
    const p = PRESETS[preset];
    return `<article class="card"><div class="thumb"><img id="th-${k}" alt="${c.label} render"><span class="ex">${p.name}</span></div><div class="body">
      <div class="top"><h3>${c.label}</h3><span class="pc">${c.pc[0]}–${c.pc[1]} bar</span></div>
      <dl class="kv"><dt>Isp</dt><dd>${c.isp}</dd><dt>Mass</dt><dd>${c.massNote}</dd><dt>Ignitions</dt><dd>${c.ign} · min throttle ${c.minThr} %</dd><dt>Propellant</dt><dd>${c.props.map(x => PROPS[x].label).join(', ')}${c.lockedProps ? ' (Hydrolox later)' : ''}</dd><dt>Real</dt><dd>${c.examples}</dd></dl>
      <div class="foot"><span class="unlock">${c.tier === 0 ? 'Start tech' : `Tier ${c.tier} · ${c.node}`}</span><button class="load" data-preset="${preset}">Load in designer</button></div></div></article>`;
  }).join('');
  host.onclick = e => { const b = e.target.closest('.load'); if (b && b.dataset.preset) loadPreset(b.dataset.preset, true); };
}

function renderThumbs() {
  const W = 480, H = 360;
  const tr = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
  tr.setPixelRatio(1); tr.setSize(W, H, false);
  tr.outputColorSpace = THREE.SRGBColorSpace; tr.toneMapping = THREE.ACESFilmicToneMapping; tr.setClearColor(0, 0);
  const ts = new THREE.Scene();
  ts.environment = makeEnvironment(tr);
  ts.add(key.clone(), rim.clone(), new THREE.HemisphereLight(0x8a96a3, 0x1a1512, 0.35));
  const cam = new THREE.PerspectiveCamera(30, W / H, 0.01, 1000);
  const jobs = [...CYCLE_ORDER.map(k => [k, PRESETS[CYCLE_PRESET[k]], {}]), ['Bleed', { prop: 'Hydrolox', cycle: 'Expander', pc: 60, eps: 60, thrust: 140, lf: 0.8, alt: 120 }, { bleed: true }]];
  const saved = M.niobium.emissiveIntensity;
  const step = () => {
    const job = jobs.shift();
    if (!job) { tr.dispose(); M.niobium.emissiveIntensity = saved; dirty = true; updatePlumes(); return; }
    const [k, p, opt] = job;
    const e = PE.evaluate({ prop: p.prop, cycle: p.cycle, pc: p.pc, eps: p.eps, thrust: p.thrust, lf: p.lf, tier: -1, fuel: p.fuel || 'Graphite' });
    const r = buildEngine(e, opt);
    ts.add(r);
    r.updateMatrixWorld(true);
    const box = new THREE.Box3().setFromObject(r);
    const sph = box.getBoundingSphere(new THREE.Sphere());
    const d = sph.radius / Math.sin(cam.fov * Math.PI / 360) * 0.9;
    const az = 0.65, pol = 1.33;
    cam.position.set(sph.center.x + d * Math.sin(pol) * Math.cos(az), sph.center.y + d * Math.cos(pol), sph.center.z + d * Math.sin(pol) * Math.sin(az));
    cam.near = d / 100; cam.far = d * 10; cam.updateProjectionMatrix(); cam.lookAt(sph.center);
    M.niobium.emissiveIntensity = 0; M.cc.emissiveIntensity = 0;
    tr.render(ts, cam);
    const img = document.getElementById('th-' + k);
    if (img) img.src = tr.domElement.toDataURL('image/png');
    ts.remove(r); disposeGroup(r);
    (window.requestIdleCallback || setTimeout)(step);
  };
  step();
}

/* ───────────────────────── Boot ───────────────────────── */
buildControls();
buildCards();
resize();
update();
requestAnimationFrame(frame);
setTimeout(renderThumbs, 400);
