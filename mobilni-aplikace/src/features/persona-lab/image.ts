export interface LabImage { previewUrl: string; dataBase64: string; width: number; height: number; bytes: number }
export async function prepareLabImage(file: File, signal?: AbortSignal): Promise<LabImage> {
  const current = () => { if (signal?.aborted) throw new DOMException('Příprava fotografie byla zrušena.', 'AbortError'); };
  current();
  if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size > 8 * 1024 * 1024)
    throw new Error('Vyberte fotografii JPEG, PNG nebo WebP do 8 MB.');
  const url = URL.createObjectURL(file);
  try {
    const image = new Image();
    await new Promise<void>((resolve, reject) => {
      const cleanup = () => { image.onload = image.onerror = null; signal?.removeEventListener('abort', abort); };
      const abort = () => { cleanup(); image.src = ''; reject(new DOMException('Příprava fotografie byla zrušena.', 'AbortError')); };
      image.onload = () => { cleanup(); resolve(); };
      image.onerror = () => { cleanup(); reject(new Error('Fotografii se nepodařilo načíst.')); };
      signal?.addEventListener('abort', abort, { once: true });
      image.src = url;
    });
    current();
    if (!image.naturalWidth || !image.naturalHeight || image.naturalWidth * image.naturalHeight > 40_000_000)
      throw new Error('Fotografie má příliš velké rozlišení. Vyberte menší snímek.');
    const canvas = document.createElement('canvas'); const context = canvas.getContext('2d');
    if (!context) throw new Error('Toto prostředí neumí připravit bezpečný obrázek pro model.');
    for (const maximum of [512, 384, 256]) {
      const scale = Math.min(1, maximum / Math.max(image.naturalWidth, image.naturalHeight));
      canvas.width = Math.max(1, Math.round(image.naturalWidth * scale)); canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
      // Drawing decoded pixels drops original EXIF/GPS and other file metadata.
      context.drawImage(image, 0, 0, canvas.width, canvas.height);
      for (const quality of [.8, .65, .5, .35, .2]) {
        const blob = await new Promise<Blob | null>(resolve => canvas.toBlob(resolve, 'image/jpeg', quality));
        current();
        if (!blob || blob.type !== 'image/jpeg' || blob.size > 45000) continue;
        const bytes = new Uint8Array(await blob.arrayBuffer());
        current();
        return { previewUrl: URL.createObjectURL(blob), dataBase64: btoa(Array.from(bytes, value => String.fromCharCode(value)).join('')),
          width: canvas.width, height: canvas.height, bytes: blob.size };
      }
    }
    throw new Error('Fotografii nelze zmenšit pod bezpečný limit 45 kB. Vyberte jiný snímek.');
  } finally { URL.revokeObjectURL(url); }
}
