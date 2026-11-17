// Where do the triangles and bytes of a GLB go?  node tools/glb-stats.js model.glb
const fs = require("fs");
const buf = fs.readFileSync(process.argv[2]);
const jsonLen = buf.readUInt32LE(12);
const g = JSON.parse(buf.slice(20, 20 + jsonLen).toString());
const binLen = buf.length - 20 - jsonLen - 8;

const meshTris = g.meshes.map((m) => m.primitives.reduce((s, p) => s + g.accessors[p.indices].count / 3, 0));
const meshVerts = g.meshes.map((m) => m.primitives.reduce((s, p) => s + g.accessors[p.attributes.POSITION].count, 0));
const uses = new Array(g.meshes.length).fill(0);
g.nodes.forEach((n) => { if (n.mesh !== undefined) uses[n.mesh]++; });

const total = meshTris.reduce((a, b) => a + b, 0);
const totalVerts = meshVerts.reduce((a, b) => a + b, 0);
const drawn = meshTris.reduce((s, t, i) => s + t * uses[i], 0);
let idxBytes = 0, attrBytes = 0;
g.meshes.forEach((m) => m.primitives.forEach((p) => {
  idxBytes += g.bufferViews[g.accessors[p.indices].bufferView].byteLength;
  for (const a of Object.values(p.attributes)) attrBytes += g.bufferViews[g.accessors[a].bufferView].byteLength;
}));
console.log(`meshes ${g.meshes.length}, nodes ${g.nodes.length}, unique tris ${total}, drawn tris ${drawn}, verts ${totalVerts}`);
console.log(`bin ${(binLen / 1e6).toFixed(1)} MB: indices ${(idxBytes / 1e6).toFixed(1)} MB, attributes ${(attrBytes / 1e6).toFixed(1)} MB, json ${(jsonLen / 1e6).toFixed(2)} MB`);
console.log(`meshes with >65535 verts: ${meshVerts.filter((v) => v > 65535).length}`);

const order = meshTris.map((t, i) => i).sort((a, b) => meshTris[b] - meshTris[a]);
let acc = 0;
console.log("\nbiggest meshes (unique tris, uses, share, cumulative):");
order.slice(0, 25).forEach((i) => {
  acc += meshTris[i];
  console.log(`${String(meshTris[i]).padStart(8)} x${String(uses[i]).padEnd(3)} ${(100 * meshTris[i] / total).toFixed(1).padStart(5)}% ${(100 * acc / total).toFixed(0).padStart(3)}%  ${g.meshes[i].name}`);
});

// Small parts: triangles in meshes whose bounding box diagonal < 15 mm
let small = 0;
g.meshes.forEach((m, i) => {
  let mn = [1e9, 1e9, 1e9], mx = [-1e9, -1e9, -1e9];
  m.primitives.forEach((p) => { const a = g.accessors[p.attributes.POSITION]; for (let k = 0; k < 3; k++) { mn[k] = Math.min(mn[k], a.min[k]); mx[k] = Math.max(mx[k], a.max[k]); } });
  const d = Math.hypot(mx[0] - mn[0], mx[1] - mn[1], mx[2] - mn[2]);
  if (d < 0.015) small += meshTris[i] * uses[i];
});
console.log(`\ndrawn tris in parts smaller than 15 mm: ${small} (${(100 * small / drawn).toFixed(0)}%)`);
