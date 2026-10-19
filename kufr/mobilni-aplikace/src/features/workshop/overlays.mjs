import { graphics } from './graphics.mjs';

export function scopedRoutes(model, state) {
  let selected = state.selectedWire ? model.routes.filter(route => route.id === state.selectedWire) : [];
  const mesh = model.meshes.find(item => item.id === state.selected);
  if (!state.selectedWire && mesh) selected = model.routes.filter(route => [route.from, route.to].some(endpoint => graphics.componentOf(endpoint, model) === mesh.name));
  if (state.selectedEndpoint) selected = model.routes.filter(route => [route.from, route.to, graphics.physicalEndpoint(route, 'from', state), graphics.physicalEndpoint(route, 'to', state)].includes(state.selectedEndpoint));
  if (['net', 'bundle'].includes(state.wireScope)) {
    const keys = new Set(selected.map(route => route[state.wireScope]));
    selected = model.routes.filter(route => route[state.wireScope] && keys.has(route[state.wireScope]));
  }
  return selected;
}
/** Derived screen geometry only. Vue renders accessible SVG and text from this snapshot.
 * @returns {import('./workshop-types').ViewportOverlay}
 */
export function overlaySnapshot(model, state, visibleMeshes, projection, view, width, height) {
  const project = point => graphics.projectPoint(point, projection, view, width, height);
  const selectedIds = new Set(scopedRoutes(model, state).map(route => route.id));
  const installed = route => state.mode !== 'assembly' || model.assemblyGuide.steps.slice(0, state.assemblyIndex + 1).some(step => (step.wires || []).includes(route.id));
  const routes = [], pins = [], flex = [], dimensions = [], orientation = [], names = [];
  for (const route of model.routes.filter(route => installed(route) && (state.wires || selectedIds.has(route.id)))) {
    const points = graphics.routePoints(route, model, state).map(project); if (!points.every(Boolean)) continue;
    const active = selectedIds.has(route.id), color = { power: '#b6322b', signal: '#287ab0', ground: '#73685d' }[route.kind] || '#a36e27';
    routes.push({ id: route.id, points: points.map(point => point.join(',')).join(' '), color, active, from: route.from, to: route.to, net: route.net || '', bundle: route.bundle || '' });
    if (active) for (const moving of route.moving_endpoints || []) {
      const index = moving.endpoint === route.from ? 0 : points.length - 1, neighbour = index === 0 ? 1 : index - 1;
      flex.push({ id: route.id, start: points[index], end: points[neighbour], label: `${route.id} · pohyblivý konec, ${route.point_lift_weights ? 'vratný oblouk R8' : 'aproximace smyčky'}` });
    }
    if (active || state.inspect) for (const [index, side] of [[0, 'from'], [points.length - 1, 'to']]) {
      const endpoint = graphics.physicalEndpoint(route, side, state);
      pins.push({ routeId: route.id, endpoint, point: points[index], color, active, label: endpoint + (endpoint !== route.from && index === 0 ? ' · odpojeno / parkováno' : '') });
    }
  }
  const c = model.case || { inside_x: 430, inside_y: 269, base_height: 114.3 };
  if (state.dimensions) for (const [start, end, label] of [
    [[0,0,c.base_height],[c.inside_x,0,c.base_height],`Průchozí horní X ${c.inside_x} mm`],
    [[0,0,c.base_height],[0,c.inside_y,c.base_height],`Průchozí horní Y ${c.inside_y} mm`],
    [[0,0,0],[0,0,c.base_height],`Spodní Z ${c.base_height} mm`],
  ]) { const a = project(start), b = project(end); if (a && b) dimensions.push({ start: a, end: b, label }); }
  for (const [position, label] of [
    [[c.inside_x/2,0,0],'PŘEDEK · ovládání'], [[c.inside_x/2,c.inside_y,0],'ZÁDA · panty'],
    [[0,c.inside_y/2,0],'LEVÁ'], [[c.inside_x,c.inside_y/2,0],'PRAVÁ'],
  ]) {
    const point = project(position);
    if (point) orientation.push({ label, x: Math.max(4, Math.min(width - 125, point[0])), y: Math.max(4, Math.min(height - 22, point[1])) });
  }
  if (state.inspect && !state.selected && !state.selectedWire && !state.selectedEndpoint) for (const mesh of visibleMeshes) {
    const center = [0,1,2].map(axis => (mesh.bounds[axis] + mesh.bounds[axis + 3]) / 2);
    const point = project(graphics.transform(graphics.movement(mesh, model, state), [...center,1]).slice(0,3));
    if (point && point[0] >= 0 && point[0] <= width && point[1] >= 0 && point[1] <= height) names.push({ id: mesh.id, label: mesh.name, point });
  }
  return { width, height, routes, pins, flex, dimensions, orientation, names, inspect: state.inspect };
}
