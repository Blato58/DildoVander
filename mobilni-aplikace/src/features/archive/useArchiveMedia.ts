import { ref } from 'vue';
import { Capacitor } from '@capacitor/core';
import { downloadName } from './client';
import { shareNativeGuestFile } from './nativeGuest';

export interface ArchiveMediaClient { capture(): () => void; blob(path: string, allowedMedia: readonly string[], publicRead?: boolean): Promise<Blob> }
export function useArchiveMedia(client: ArchiveMediaClient) {
  const fileUrl = ref('');
  const fileName = ref('');
  const artworkUrl = ref('');
  const artworkError = ref(false);
  let file: File | undefined;
  let artworkGeneration = 0;
  let transferGeneration = 0;
  const clearFile = () => { transferGeneration++; if (fileUrl.value) URL.revokeObjectURL(fileUrl.value); fileUrl.value = ''; file = undefined; };
  const clearArtwork = () => { artworkGeneration++; if (artworkUrl.value) URL.revokeObjectURL(artworkUrl.value); artworkUrl.value = ''; artworkError.value = false; };
  const clear = () => { clearFile(); clearArtwork(); };
  async function artwork(path?: string, publicRead = false) {
    clearArtwork(); if (!path) return;
    const generation = artworkGeneration; const check = client.capture();
    try {
      const blob = await client.blob(path, ['image/png', 'image/jpeg', 'image/webp', 'image/svg+xml'], publicRead); check();
      if (generation === artworkGeneration) artworkUrl.value = URL.createObjectURL(blob);
    } catch (error) { if (generation === artworkGeneration && (error as Error).name !== 'AbortError') artworkError.value = true; }
  }
  async function download(path: string, name: string, allowed: string[], publicRead = false) {
    clearFile(); const generation = transferGeneration; const check = client.capture();
    const blob = await client.blob(path, allowed, publicRead); check();
    if (generation !== transferGeneration) return;
    fileName.value = downloadName(name, blob.type); file = new File([blob], fileName.value, { type: blob.type }); fileUrl.value = URL.createObjectURL(blob);
  }
  const canShareFile = () => !!file && (Capacitor.isNativePlatform() || !!navigator.canShare?.({ files: [file] }));
  async function shareFile() { if (file && canShareFile()) { const check = client.capture(); if (Capacitor.isNativePlatform()) await shareNativeGuestFile(file, check); else await navigator.share({ files: [file] }); check(); } }
  return { fileUrl, fileName, artworkUrl, artworkError, clearFile, clearArtwork, clear, artwork, download, canShareFile, shareFile,
    native: Capacitor.isNativePlatform() };
}
