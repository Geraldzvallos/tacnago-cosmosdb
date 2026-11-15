// Página mínima de TacnaGo: consume el API REST, que a su vez lee y escribe en Azure Cosmos DB.
const $ = (s) => document.querySelector(s);
let categoriaActual = '';

const esc = (t) => String(t ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// Llama al API y muestra el costo en RU que devuelve el encabezado x-ru-charge
async function api(url, opciones) {
  const r = await fetch(url, opciones);
  const ru = r.headers.get('x-ru-charge');
  if (ru) $('#ru').textContent = `${ru} RU`;
  if (!r.ok) {
    let msg = `${r.status} ${r.statusText}`;
    try { msg = (await r.json()).error || msg; } catch { /* sin cuerpo */ }
    throw new Error(msg);
  }
  return r.status === 204 ? null : r.json();
}

function mostrarError(e) {
  const el = $('#error');
  el.textContent = e ? `Error: ${e.message}` : '';
  el.hidden = !e;
}

async function cargarCategorias() {
  const cats = await api('/api/categorias');
  const total = cats.reduce((s, c) => s + c.total, 0);
  $('#chips').innerHTML =
    `<button class="chip ${categoriaActual === '' ? 'activa' : ''}" data-cat="">Todas (${total})</button>` +
    cats.map((c) => `<button class="chip ${c.categoria === categoriaActual ? 'activa' : ''}" data-cat="${esc(c.categoria)}">${esc(c.categoria)} (${c.total})</button>`).join('');
  $('#categorias').innerHTML = cats.map((c) => `<option value="${esc(c.categoria)}">`).join('');
}

function pintar(lugares) {
  $('#lista').innerHTML = lugares.length === 0
    ? '<p>No hay lugares para mostrar.</p>'
    : lugares.map((l) => `
      <article class="card">
        <span class="badge">${esc(l.categoria)}</span>
        <h3>${esc(l.nombre)}</h3>
        <p>${esc(l.descripcion)}</p>
        <div class="tags">${(l.tags || []).map((t) => `<span data-tag="${esc(t)}">#${esc(t)}</span>`).join('')}</div>
        <div class="acciones">
          <span>${esc([l.distrito, l.provincia].filter(Boolean).join(', '))}</span>
          <button data-del="${esc(l.id)}" data-cat="${esc(l.categoria)}" title="Eliminar">Eliminar</button>
        </div>
      </article>`).join('');
}

async function cargarLugares() {
  mostrarError(null);
  try {
    const url = categoriaActual ? `/api/lugares?categoria=${encodeURIComponent(categoriaActual)}` : '/api/lugares';
    const lugares = await api(url);
    $('#modo').textContent = categoriaActual
      ? `Consulta en una sola partición: categoria = "${categoriaActual}" · ${lugares.length} lugares`
      : `Consulta entre todas las particiones · ${lugares.length} lugares`;
    pintar(lugares);
  } catch (e) { mostrarError(e); }
}

async function buscarTag(tag) {
  mostrarError(null);
  try {
    const lugares = await api(`/api/lugares/buscar?tag=${encodeURIComponent(tag)}`);
    $('#modo').textContent = `Búsqueda con ARRAY_CONTAINS(c.tags, "${tag}") · ${lugares.length} lugares`;
    pintar(lugares);
  } catch (e) { mostrarError(e); }
}

$('#chips').addEventListener('click', (ev) => {
  const b = ev.target.closest('[data-cat]');
  if (!b) return;
  categoriaActual = b.dataset.cat;
  document.querySelectorAll('.chip').forEach((c) => c.classList.toggle('activa', c === b));
  cargarLugares();
});

$('#lista').addEventListener('click', async (ev) => {
  const t = ev.target.closest('[data-tag]');
  if (t) { $('#tag').value = t.dataset.tag; return buscarTag(t.dataset.tag); }
  const d = ev.target.closest('[data-del]');
  if (!d) return;
  try {
    await api(`/api/lugares/${encodeURIComponent(d.dataset.cat)}/${encodeURIComponent(d.dataset.del)}`, { method: 'DELETE' });
    await cargarCategorias();
    await cargarLugares();
  } catch (e) { mostrarError(e); }
});

$('#form-buscar').addEventListener('submit', (ev) => {
  ev.preventDefault();
  const tag = $('#tag').value.trim();
  tag ? buscarTag(tag) : cargarLugares();
});

$('#form-nuevo').addEventListener('submit', async (ev) => {
  ev.preventDefault();
  const f = new FormData(ev.target);
  const lugar = {
    nombre: f.get('nombre'),
    categoria: f.get('categoria'),
    provincia: f.get('provincia') || null,
    descripcion: f.get('descripcion') || null,
    tags: String(f.get('tags') || '').split(',').map((t) => t.trim()).filter(Boolean),
  };
  try {
    const creado = await api('/api/lugares', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(lugar) });
    ev.target.reset();
    categoriaActual = creado.categoria;
    await cargarCategorias();
    await cargarLugares();
  } catch (e) { mostrarError(e); }
});

async function cargarRespaldos() {
  const tbody = $('#lista-respaldos');
  try {
    const r = await fetch('/api/respaldos');
    const lista = r.ok ? await r.json() : [];
    tbody.innerHTML = lista.length === 0
      ? '<tr><td colspan="5">Todavía no hay respaldos.</td></tr>'
      : lista.map((b) => `<tr>
          <td>${esc(b.nombre)}</td>
          <td>${b.fecha ? new Date(b.fecha).toLocaleString('es-PE') : ''}</td>
          <td>${b.documentos ?? ''}</td>
          <td>${b.tamanoKB} KB</td>
          <td>${esc(b.nivel)}</td></tr>`).join('');
  } catch { tbody.innerHTML = '<tr><td colspan="5">No se pudieron cargar los respaldos.</td></tr>'; }
}

(async () => {
  try { await cargarCategorias(); } catch (e) { mostrarError(e); }
  await cargarLugares();
  await cargarRespaldos();
})();
