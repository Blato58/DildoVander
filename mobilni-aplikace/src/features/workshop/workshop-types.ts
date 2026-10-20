export type ScreenPoint = [number, number];
export interface ViewportOverlay {
  width: number;
  height: number;
  inspect: boolean;
  routes: { id: string; points: string; color: string; active: boolean; from: string; to: string; net: string; bundle: string }[];
  pins: { routeId: string; endpoint: string; point: ScreenPoint; color: string; active: boolean; label: string }[];
  flex: { id: string; start: ScreenPoint; end: ScreenPoint; label: string }[];
  dimensions: { start: ScreenPoint; end: ScreenPoint; label: string }[];
  orientation: { label: string; x: number; y: number }[];
  names: { id: string; label: string; point: ScreenPoint }[];
}
