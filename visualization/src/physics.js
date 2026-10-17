/* ProcEngines mockup: performance + mass model (DESIGN.md §4–§6, §8.2).
   Plain script; exposes window.PE. Frozen-gamma fits, tuned against the §5.2 sanity table. */
(function () {
  const G0 = 9.80665;
  const ATM = 101325;

  // Frozen-flow fits. cstar table: [Pc bar, c* m/s]. rhoBulk t/m³ at game O/F (§4.2).
  const PROPS = {
    Kerolox:  { label: 'Kerolox',  sub: 'RP-1 / LOX', gamma: 1.215, cstar: [[5,1745],[20,1785],[50,1810],[100,1828],[300,1842]], rho: 1.03, lstar: 1.1, tier: 0, node: 'start', color: '#f2a145' },
    Methalox: { label: 'Methalox', sub: 'CH₄ / LOX',  gamma: 1.205, cstar: [[5,1770],[10,1795],[50,1835],[100,1850],[300,1868]], rho: 0.83, lstar: 1.0, tier: 3, node: 'gptt_methalox', color: '#8d8cf2' },
    Hydrolox: { label: 'Hydrolox', sub: 'LH₂ / LOX',  gamma: 1.205, cstar: [[5,2270],[20,2300],[50,2318],[100,2328],[300,2340]], rho: 0.36, lstar: 0.85, tier: 2, node: 'gptt_hydrolox1', color: '#7fd3f0' },
    'NTR-H2': { label: 'NTR-H₂',   sub: 'LH₂ through a reactor', gamma: 1.38, cstar: null, rho: 0.071, lstar: 0, tier: 3, node: 'gptt_ntr1', color: '#e58fc8' },
  };

  // §5.3 cycles table.
  const CYCLES = {
    PressureFed: { label: 'Pressure-fed', short: 'PF', pc: [5, 25], ispMul: 1, dp: 0, a: 0, minThr: 10, ign: '30+', ignN: 30, props: ['Kerolox','Methalox','Hydrolox'], tier: 0, node: 'start',
      isp: 'None, but low Pc keeps the engine big and limits ε at sea level', massNote: 'Lightest machinery; pays a helium-bottle mass term instead',
      examples: 'AJ10 (Apollo SPS), Kestrel, LMDE, Aestus', kit: ['Helium bottles ×2', 'Main valves', 'No pumps'] },
    Electric: { label: 'Electric pump-fed', short: 'EP', pc: [10, 130], ispMul: 1, dp: 1.25, a: 0.55, minThr: 15, ign: 'Many', ignN: 20, props: ['Kerolox','Methalox'], tier: 4, node: 'gptt_power',
      isp: 'None', massNote: 'Pump + motor + inverter; draws ElectricCharge, pays off only for small engines',
      examples: 'Rutherford (Electron)', kit: ['Pumps ×2', 'Motor cans ×2', 'Inverter + battery box'] },
    GasGenerator: { label: 'Gas generator', short: 'GG', pc: [30, 110], ispMul: 0.98, dp: 1.3, a: 0.9, minThr: 60, ign: '1–3', ignN: 2, props: ['Kerolox','Methalox','Hydrolox'], tier: 0, node: 'start',
      isp: '−1.5 to −3 %: turbine exhaust is dumped overboard', massNote: 'Moderate pump mass, separate exhaust plume',
      examples: 'Merlin 1D, F-1, RS-68, Vulcain 2, J-2', kit: ['Single-shaft turbopump', 'Gas generator', 'Turbine exhaust duct'] },
    Expander: { label: 'Expander (closed)', short: 'EX', pc: [20, 70], ispMul: 1, dp: 1.5, a: 1.1, minThr: 30, ign: '10+', ignN: 12, props: ['Hydrolox','Methalox'], tier: 2, node: 'gptt_hydrolox1',
      isp: 'None', massNote: 'Very light and reliable; thrust capped by chamber heat pickup',
      examples: 'RL10, Vinci (LE-5B, LE-9 as bleed)', kit: ['Small turbopump', 'Ox pump', 'Regen jacket manifolds'] },
    Staged: { label: 'Staged combustion', short: 'SC', pc: [120, 270], ispMul: 1, dp: 2.0, a: 1.5, minThr: 50, ign: '1–3', ignN: 2, props: ['Kerolox','Methalox','Hydrolox'], tier: 4, node: 'gptt_metox',
      isp: 'None (no dump)', massNote: 'Heavy turbomachinery, small dense engine, high TWR',
      examples: 'RS-25, RD-180, NK-33, BE-4', kit: ['Big turbopump', 'Preburner on top', 'Hot-gas duct to injector'] },
    FullFlow: { label: 'Full-flow staged', short: 'FF', pc: [200, 330], ispMul: 1, dp: 1.6, a: 0.85, minThr: 40, ign: '2–5', ignN: 3, props: ['Methalox'], lockedProps: { Hydrolox: 'possible later' }, tier: 6, node: 'late propulsion node',
      isp: 'None', massNote: 'Two preburners, two turbopumps; best TWR, latest tech',
      examples: 'Raptor 2 (RD-270 never flew)', kit: ['Fuel-rich turbopump + preburner', 'Ox-rich turbopump + preburner', 'Two hot-gas ducts'] },
    Nuclear: { label: 'Nuclear thermal', short: 'NT', pc: [20, 90], ispMul: 1, dp: 1.4, a: 1.1, minThr: 30, ign: '5–20', ignN: 10, props: ['NTR-H2'], tier: 3, node: 'gptt_ntr1',
      isp: '850–950 s: hydrogen only, no combustion (§5.4)', massNote: 'Reactor-dominated mass, low TWR',
      examples: 'NERVA, SNRE, RD-0410', kit: ['Reactor vessel', 'Shield disc', 'Control-drum ring', 'One H₂ pump'] },
  };
  const CYCLE_ORDER = ['PressureFed', 'Electric', 'GasGenerator', 'Expander', 'Staged', 'FullFlow', 'Nuclear'];
  const PROP_ORDER = ['Kerolox', 'Methalox', 'Hydrolox', 'NTR-H2'];

  // NTR fuel elements (§5.4).
  const FUELS = {
    Graphite: { label: 'Graphite composite', tc: 2450, core0: 800, kth: 4.5, tier: 3, node: 'gptt_ntr1' },
    Cermet:   { label: 'CERMET',             tc: 2800, core0: 1000, kth: 3.0, tier: 5, node: 'gptt_ntr3' },
    Carbide:  { label: 'Advanced carbide',   tc: 3100, core0: 900, kth: 2.5, tier: 5, node: 'gptt_ntr4' },
  };

  // §6 tiers. Sandbox ignores caps.
  const TIERS = [
    { n: 0, nodes: 'start, gptt_liquid0', pcMax: 50, epsMax: 25, thrMax: 400, etaC: 0.94, etaP: 0.55, regenEps: 8, cone: true, ext: null },
    { n: 1, nodes: 'gptt_liquid1, gptt_fuelPump', pcMax: 70, epsMax: 60, thrMax: 1200, etaC: 0.95, etaP: 0.58, regenEps: 10, cone: false, ext: null },
    { n: 2, nodes: 'gptt_hydrolox1, gptt_liquidVac1', pcMax: 70, epsMax: 60, thrMax: 1500, etaC: 0.955, etaP: 0.61, regenEps: 15, cone: false, ext: null },
    { n: 3, nodes: 'gptt_liquid3, gptt_methalox, gptt_ntr1', pcMax: 110, epsMax: 150, thrMax: 2500, etaC: 0.97, etaP: 0.64, regenEps: 20, cone: false, ext: 'Niobium' },
    { n: 4, nodes: 'gptt_metox, gptt_liquid4, gptt_power', pcMax: 270, epsMax: 150, thrMax: 5000, etaC: 0.975, etaP: 0.67, regenEps: 20, cone: false, ext: 'Niobium' },
    { n: 5, nodes: 'gptt_liquidVac4, gptt_ntr3', pcMax: 270, epsMax: 300, thrMax: 5000, etaC: 0.98, etaP: 0.71, regenEps: 30, cone: false, ext: 'C-C' },
    { n: 6, nodes: 'late propulsion node', pcMax: 330, epsMax: 300, thrMax: 5000, etaC: 0.985, etaP: 0.75, regenEps: 30, cone: false, ext: 'C-C' },
  ];
  const SANDBOX = { n: -1, nodes: 'Sandbox', pcMax: 330, epsMax: 300, thrMax: 5000, etaC: 0.975, etaP: 0.70, regenEps: 25, cone: false, ext: 'auto' };
  function tierCaps(t) { return t < 0 ? SANDBOX : TIERS[t]; }

  function lerpTable(tab, x) {
    const lx = Math.log(x);
    if (x <= tab[0][0]) return tab[0][1];
    for (let i = 1; i < tab.length; i++) {
      if (x <= tab[i][0]) {
        const a = Math.log(tab[i - 1][0]), b = Math.log(tab[i][0]);
        const f = (lx - a) / (b - a);
        return tab[i - 1][1] + f * (tab[i][1] - tab[i - 1][1]);
      }
    }
    return tab[tab.length - 1][1];
  }

  // §5.1 area–Mach relation, supersonic branch.
  function areaRatio(M, g) {
    return (1 / M) * Math.pow((2 / (g + 1)) * (1 + (g - 1) / 2 * M * M), (g + 1) / (2 * (g - 1)));
  }
  function machFromEps(eps, g) {
    let lo = 1.0001, hi = 30;
    for (let i = 0; i < 80; i++) {
      const mid = 0.5 * (lo + hi);
      if (areaRatio(mid, g) < eps) lo = mid; else hi = mid;
    }
    return 0.5 * (lo + hi);
  }
  function peOverPc(eps, g) {
    const M = machFromEps(eps, g);
    return Math.pow(1 + (g - 1) / 2 * M * M, -g / (g - 1));
  }
  function cfVac(eps, g) {
    const pr = peOverPc(eps, g);
    const t = 2 * g * g / (g - 1) * Math.pow(2 / (g + 1), (g + 1) / (g - 1)) * (1 - Math.pow(pr, (g - 1) / g));
    return Math.sqrt(t) + eps * pr;
  }
  // Effective exit ratio where wall pressure = kSep·pa (separated flow, §5.2).
  function epsAtPressure(pRatio, g, epsMax) {
    let lo = 1.0001, hi = epsMax;
    for (let i = 0; i < 60; i++) {
      const mid = Math.sqrt(lo * hi);
      if (peOverPc(mid, g) > pRatio) lo = mid; else hi = mid;
    }
    return Math.sqrt(lo * hi);
  }

  const K_SEP = 0.35;

  // Ambient pressure on Earth (Sol RSS-scale home), Pa. Troposphere + exponential.
  function earthPressure(hKm) {
    const h = hKm * 1000;
    if (h <= 11000) return ATM * Math.pow(1 - 2.25577e-5 * h, 5.25588);
    const p11 = ATM * Math.pow(1 - 2.25577e-5 * 11000, 5.25588);
    return p11 * Math.exp(-(h - 11000) / 6340);
  }
  function altitudeForPressure(p) {
    if (p >= ATM) return 0;
    let lo = 0, hi = 200;
    for (let i = 0; i < 60; i++) {
      const mid = 0.5 * (lo + hi);
      if (earthPressure(mid) > p) lo = mid; else hi = mid;
    }
    return 0.5 * (lo + hi);
  }

  // Rao bell angles vs ε at Lf = 0.8 (digitised curve, §8.2), shifted for other Lf.
  const RAO = [[2, 17, 15], [4, 21, 14], [10, 26, 11], [20, 28.5, 9.5], [50, 32.5, 8], [100, 35, 7], [200, 37, 6.5], [300, 38, 6]];
  function raoAngles(eps, lf) {
    const tn = lerpTable(RAO.map(r => [r[0], r[1]]), eps);
    const te = lerpTable(RAO.map(r => [r[0], r[2]]), eps);
    const d = (0.8 - lf) / 0.2; // +1 at Lf 0.6, −0.5 at Lf 0.9
    return { thetaN: tn + d * 5, thetaE: te + d * 4 };
  }

  /** Evaluate a design. d: {prop, cycle, pc (bar), eps, thrust (kN vac), lf, tier, fuel, cluster} */
  function evaluate(d) {
    const caps = tierCaps(d.tier);
    const cyc = CYCLES[d.cycle];
    const prop = PROPS[d.prop];
    const nuclear = d.cycle === 'Nuclear';
    const Pc = d.pc * 1e5;
    const eps = d.eps;
    const F = d.thrust * 1000;
    const cone = caps.cone;

    let g = prop.gamma, cstar, fuel = null;
    if (nuclear) {
      fuel = FUELS[d.fuel] || FUELS.Graphite;
      const M = 2.016, R = 8314.46, Tc = fuel.tc;
      const Gam = Math.sqrt(g) * Math.pow(2 / (g + 1), (g + 1) / (2 * (g - 1)));
      cstar = 1.05 * Math.sqrt(R * Tc / M) / Gam; // ×1.05: real-gas / CEA fit
    } else {
      cstar = lerpTable(prop.cstar, d.pc);
    }
    const etaC = caps.etaC;
    // Nozzle efficiency: divergence × boundary layer. Cone 15° ≈ 0.983; Rao 80 % ≈ 0.987.
    const ang = raoAngles(eps, d.lf);
    const div = cone ? 0.983 : 0.985 + 0.02 * (d.lf - 0.6) / 0.3 * 0.25;
    const bl = 1 - (0.008 + 0.012 * Math.exp(-d.pc / 60));
    const etaCf = div * bl;

    const cfv = cfVac(eps, g);
    const prE = peOverPc(eps, g);
    const pe = prE * Pc;
    const At = F / (etaCf * cfv * Pc);
    const Rt = Math.sqrt(At / Math.PI);
    const Re = Math.sqrt(eps) * Rt;
    const mdot = Pc * At / (etaC * cstar);

    function ispAt(pa) {
      let cf, sep = false, epsEff = eps;
      if (pe < K_SEP * pa) {
        sep = true;
        epsEff = epsAtPressure(K_SEP * pa / Pc, g, eps);
        cf = cfVac(epsEff, g) - epsEff * pa / Pc;
      } else {
        cf = cfv - eps * pa / Pc;
      }
      const isp = Math.max(0, etaC * cstar * etaCf * cf / G0 * cyc.ispMul);
      return { isp, sep, epsEff };
    }
    const vac = ispAt(0), sl = ispAt(ATM);
    const ispVac = vac.isp, ispSL = sl.isp;
    const thrustSL = F * ispSL / ispVac;
    const sepAltKm = pe < K_SEP * ATM ? altitudeForPressure(pe / K_SEP) : 0;

    // Chamber geometry (§8.3)
    const Dt_cm = 2 * Rt * 100;
    let epsC = 8 * Math.pow(Dt_cm, -0.6) + 1.25;
    epsC = Math.max(2.0, Math.min(epsC, 6));
    let Rc = Math.sqrt(epsC) * Rt;

    // Pump power (§5.3). Pa·m³/s → W.
    const etaP = caps.etaP;
    const dpPump = (cyc.dp * Pc) + 10e5;
    const Ppump = cyc.dp > 0 ? mdot * dpPump / (prop.rho * 1000 * etaP) : 0; // W
    const PpumpKW = Ppump / 1000;

    // NTR thermal power and reactor size
    let Pth = 0, reactorR = 0, reactorH = 0;
    if (nuclear) {
      Pth = mdot * 14000 * fuel.tc / 1e6; // MW
      reactorR = Math.max(Rc, 0.05 * Math.cbrt(Pth) + 0.08);
      reactorH = 2.4 * reactorR;
      Rc = reactorR;
    }

    // Mass model (§5.5), kg
    const Fk = d.thrust;
    const mass = {};
    mass.chamber = nuclear ? 0 : 0.14 * Fk * (prop.lstar / 1.0) + 25 * (2 * Math.PI * Rc * (Rc * 2.2)) * Math.max(0.6, Math.sqrt(d.pc / 100));
    // Nozzle area ≈ k·At·(ε−1); regen at 18 kg/m², radiative extension at 7 kg/m².
    const extAllowed = caps.ext && eps > 40;
    const regenEps = extAllowed ? Math.min(eps, caps.regenEps) : eps;
    const kA = cone ? 4.2 : 3.2 * (d.lf / 0.8);
    const aRegen = kA * At * (regenEps - 1);
    const aExt = extAllowed ? kA * At * (eps - regenEps) : 0;
    mass.nozzle = 18 * aRegen + 7 * aExt;
    if (d.cycle === 'Electric') {
      mass.turbo = cyc.a * Math.pow(PpumpKW, 0.65) + PpumpKW / (caps.n >= 6 ? 8 : 5) ;
    } else {
      mass.turbo = cyc.a * Math.pow(Math.max(PpumpKW, 1), 0.65) * (cyc.dp > 0 ? 1 : 0);
    }
    if (d.cycle === 'PressureFed') mass.press = 0.0025 * mdot * 400 * d.pc;
    if (nuclear) {
      mass.reactor = fuel.core0 + fuel.kth * Pth;
      mass.shield = 0.1 * mass.reactor;
    }
    let sum = 0; for (const k in mass) sum += mass[k];
    mass.structure = 0.08 * sum + 15 + 0.02 * Fk;
    let total = 0; for (const k in mass) total += mass[k];

    const N = d.cluster || 1;
    const plate = N > 1 ? 0.012 * Fk * N + 40 * N : 0;
    const totalCluster = total * N + plate;

    const twrVac = (F * N) / (totalCluster * G0);
    const twrSL = (thrustSL * N) / (totalCluster * G0);

    // Lengths (for readout): injector→exit
    const nozzleLen = cone ? (Re - Rt) / Math.tan(15 * Math.PI / 180) : d.lf * (Math.sqrt(eps) - 1) * Rt / Math.tan(15 * Math.PI / 180);
    const chamberLen = nuclear ? reactorH + 1.2 * Rc : Math.max(0.6 * Rc, (prop.lstar * At) / (Math.PI * Rc * Rc)) + 1.6 * Rc;
    const length = nozzleLen + chamberLen + 0.8 * Rc;

    const ecDraw = d.cycle === 'Electric' ? Ppump / 0.9 / 1000 : 0; // kW = EC/s

    // Warnings
    const warn = [];
    if (sl.sep) warn.push({ lvl: 'warn', text: `Flow separates below ${sepAltKm.toFixed(1)} km: exit pressure ${(pe / 1e5).toFixed(2)} bar is under 0.35 × ambient.` });
    if (d.cycle === 'Expander') {
      const cap = d.prop === 'Hydrolox' ? 300 : 100;
      if (d.thrust >= cap - 0.5) warn.push({ lvl: 'info', text: `Expander thrust limit: ${cap} kN for ${d.prop}. Heat pickup grows like F^2/3, turbine demand like F.` });
    }
    if (d.cycle === 'Electric') {
      const battT = ecDraw * 60 / 20000; // stock: ~20 EC per kg battery
      warn.push({ lvl: battT > 1 ? 'warn' : 'info', text: `Draws ${fmt(ecDraw, 0)} EC/s. A 1-minute burn needs ≈ ${fmt(battT, battT < 1 ? 2 : 1)} t of stock batteries.` });
    }
    if (nuclear) warn.push({ lvl: 'info', text: `Reactor at ${fmt(Pth, 0)} MW thermal. Kerbalism radiation emitter while running; decay heat after shutdown.` });
    if (extAllowed) warn.push({ lvl: 'info', text: `Radiative ${extMaterial(caps, eps)} extension below ε ${regenEps.toFixed(0)}: vacuum-optimised, glows when hot.` });
    if (d.cycle === 'PressureFed') warn.push({ lvl: 'info', text: `Pressurant: ≈ ${fmt(mass.press, 0)} kg of helium bottles sized for a 400 s burn.` });

    return {
      d, caps, cyc, prop, nuclear, fuel, cone,
      g, cstar, etaC, etaCf, cfv, pe, Pc, At, Rt, Re, Rc, epsC, mdot, De: 2 * Re,
      ispVac, ispSL, sepSL: sl.sep, epsSepSL: sl.epsEff, sepAltKm, thrustVac: F, thrustSL,
      ispAt, PpumpKW, Pth, reactorR, reactorH, mass, total, totalCluster, twrVac, twrSL,
      regenEps, extAllowed, extMat: extAllowed ? extMaterial(caps, eps) : null,
      nozzleLen, chamberLen, length, ecDraw, angles: ang, warn,
    };
  }
  function extMaterial(caps, eps) {
    if (caps.ext === 'auto') return eps > 150 ? 'C-C' : 'Niobium';
    return caps.ext;
  }
  function fmt(v, dp) {
    if (!isFinite(v)) return '–';
    return v.toLocaleString('en-US', { minimumFractionDigits: dp, maximumFractionDigits: dp });
  }

  /** Nozzle + chamber contour, axial x (m, + downstream of throat), radius r (m). §8.2/§8.3 */
  function contour(ev) {
    const Rt = ev.Rt, Rc = ev.Rc, eps = ev.d.eps;
    const rad = Math.PI / 180;
    const pts = { chamber: [], conv: [], throat: [], bell: [], ext: [] };
    const alpha = 30 * rad;
    // Converging arc 1.5·Rt from −(90+α) to −90°
    const A = { x: -1.5 * Rt * Math.sin(alpha), r: 2.5 * Rt - 1.5 * Rt * Math.cos(alpha) };
    const Rf = 0.5 * Rc;
    const Fr = Rc - Rf * (1 - Math.cos(alpha));
    const Fx = A.x - (Fr - A.r) / Math.tan(alpha);
    const xc = Fx - Rf * Math.sin(alpha);
    let cylLen;
    if (ev.nuclear) cylLen = ev.reactorH;
    else {
      const convLen = -xc;
      const vConv = Math.PI / 3 * convLen * (Rc * Rc + Rc * Rt + Rt * Rt);
      cylLen = Math.max(0.6 * Rc, (ev.prop.lstar * ev.At - vConv) / (Math.PI * Rc * Rc));
    }
    const xInj = xc - cylLen;
    pts.chamber.push({ x: xInj, r: Rc }, { x: xc, r: Rc });
    for (let i = 1; i <= 8; i++) { const p = alpha * i / 8; pts.conv.push({ x: xc + Rf * Math.sin(p), r: Rc - Rf + Rf * Math.cos(p) }); }
    for (let i = 0; i <= 6; i++) { const t = i / 6; pts.conv.push({ x: Fx + (A.x - Fx) * t, r: Fr + (A.r - Fr) * t }); }
    for (let i = 1; i <= 10; i++) { const p = -(90 * rad + alpha) + alpha * i / 10; pts.conv.push({ x: 1.5 * Rt * Math.cos(p), r: 2.5 * Rt + 1.5 * Rt * Math.sin(p) }); }
    // Throat downstream arc 0.382·Rt
    let thN = ev.cone ? 15 : ev.angles.thetaN, thE = ev.cone ? 15 : ev.angles.thetaE;
    for (let i = 0; i <= 8; i++) { const p = thN * rad * i / 8; pts.throat.push({ x: 0.382 * Rt * Math.sin(p), r: 1.382 * Rt - 0.382 * Rt * Math.cos(p) }); }
    const N = pts.throat[pts.throat.length - 1];
    const Re = Math.sqrt(eps) * Rt;
    let E, Q;
    if (ev.cone) {
      E = { x: N.x + (Re - N.r) / Math.tan(15 * rad), r: Re };
      Q = { x: 0.5 * (N.x + E.x), r: 0.5 * (N.r + E.r) };
    } else {
      const Ln = ev.d.lf * (Math.sqrt(eps) - 1) * Rt / Math.tan(15 * rad);
      E = { x: Ln, r: Re };
      const tn = Math.tan(thN * rad), te = Math.tan(thE * rad);
      const qx = (E.r - N.r + N.x * tn - E.x * te) / (tn - te);
      Q = { x: qx, r: N.r + tn * (qx - N.x) };
    }
    // Quadratic Bézier, ~40 samples denser near N; split at ε_regen.
    const rSplit = ev.extAllowed ? Math.sqrt(ev.regenEps) * Rt : Infinity;
    const bez = t => ({ x: (1 - t) * (1 - t) * N.x + 2 * t * (1 - t) * Q.x + t * t * E.x, r: (1 - t) * (1 - t) * N.r + 2 * t * (1 - t) * Q.r + t * t * E.r });
    let split = null;
    const S = 44;
    for (let i = 0; i <= S; i++) {
      const t = Math.pow(i / S, 1.35);
      const p = bez(t);
      if (p.r <= rSplit) pts.bell.push(p);
      else {
        if (!split) {
          // bisection for exact split point
          let lo = 0, hi = t;
          for (let k = 0; k < 30; k++) { const m = 0.5 * (lo + hi); if (bez(m).r < rSplit) lo = m; else hi = m; }
          split = bez(0.5 * (lo + hi));
          pts.bell.push(split); pts.ext.push(split);
        }
        pts.ext.push(p);
      }
    }
    return { pts, N, Q, E, A, xInj, xc, cylLen, Rt, Rc, Re, split, thN, thE };
  }

  window.PE = { PROPS, CYCLES, CYCLE_ORDER, PROP_ORDER, FUELS, TIERS, SANDBOX, tierCaps, evaluate, contour, earthPressure, altitudeForPressure, peOverPc, cfVac, fmt, G0, ATM, K_SEP };
})();
