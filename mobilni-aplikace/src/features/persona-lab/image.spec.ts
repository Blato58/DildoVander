import { afterEach, describe, expect, it, vi } from 'vitest';
import { prepareLabImage } from './image';
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });
describe('consented image preparation', () => {
  it('rejects unsupported files before decoding or creating a preview', async () => {
    const create = vi.spyOn(URL, 'createObjectURL');
    await expect(prepareLabImage(new File(['private'], 'content.svg', { type: 'image/svg+xml' }))).rejects.toThrow('JPEG');
    expect(create).not.toHaveBeenCalled();
  });
  it('renders only decoded pixels into a small JPEG and releases the original file URL', async () => {
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValueOnce('blob:original').mockReturnValueOnce('blob:prepared');
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    vi.stubGlobal('Image', class {
      naturalWidth = 1200; naturalHeight = 800; onload: (() => void) | null = null; onerror = null;
      set src(_value: string) { queueMicrotask(() => this.onload?.()); }
    });
    const draw = vi.fn();
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage: draw } as unknown as CanvasRenderingContext2D);
    vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation(function (this: HTMLCanvasElement, callback: BlobCallback) {
      expect(Math.max(this.width, this.height)).toBe(512);
      callback(new Blob([new Uint8Array(40000)], { type: 'image/jpeg' }));
    });
    const prepared = await prepareLabImage(new File(['PRIVATE ORIGINAL METADATA'], 'photo.png', { type: 'image/png' }));
    expect(prepared).toMatchObject({ previewUrl: 'blob:prepared', width: 512, height: 341, bytes: 40000 });
    expect(draw).toHaveBeenCalledOnce(); expect(create).toHaveBeenCalledTimes(2); expect(revoke).toHaveBeenCalledWith('blob:original');
    expect(atob(prepared.dataBase64)).not.toContain('PRIVATE');
  });
  it('cancels an unfinished decode and revokes its file URL', async () => {
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:pending');
    const revoke = vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => {});
    vi.stubGlobal('Image', class { onload = null; onerror = null; set src(_value: string) {} });
    const controller = new AbortController(); const pending = prepareLabImage(new File(['image'], 'test.jpg', { type: 'image/jpeg' }), controller.signal);
    controller.abort(); await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(revoke).toHaveBeenCalledWith('blob:pending');
  });
});
