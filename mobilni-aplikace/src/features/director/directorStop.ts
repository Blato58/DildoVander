import { shallowRef } from 'vue';
export interface DirectorStopParticipant { canStop(): boolean; busy(): boolean; stop(): Promise<void>; clear(): void }
export const directorStop = shallowRef<DirectorStopParticipant | null>(null);
export function registerDirectorStop(participant: DirectorStopParticipant): void {
  if (directorStop.value !== participant) directorStop.value?.clear();
  directorStop.value = participant;
}
export function clearDirectorStop(): void {
  const previous = directorStop.value; directorStop.value = null; previous?.clear();
}
