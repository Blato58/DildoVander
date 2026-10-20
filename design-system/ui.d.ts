export type Tone = 'neutral' | 'info' | 'success' | 'warning' | 'danger';
export interface ButtonOptions {
  label: unknown;
  variant?: 'primary' | 'secondary' | 'danger' | 'ghost';
  size?: 'normal' | 'small';
  disabled?: boolean;
  loading?: boolean;
  type?: 'button' | 'submit' | 'reset';
  id?: string;
  attributes?: Record<string, string | number | boolean | null | undefined>;
}
export declare function escapeHtml(value: unknown): string;
export declare function button(options: ButtonOptions): string;
export declare function statusBadge(options: { label: unknown; tone?: Tone }): string;
/** action must be trusted application/framework HTML; live:false suppresses repeated snapshot announcements. */
export declare function notice(options: { title?: unknown; message: unknown; detail?: unknown; action?: string; tone?: Tone; live?: boolean }): string;
/** action must be trusted framework/application HTML. */
export declare function emptyState(options: { title: unknown; message: unknown; action?: string }): string;
export declare function wandCard(options: { title: unknown; subtitle?: unknown; number?: string | number; imageUrl?: string; selected?: boolean; disabled?: boolean; interactive?: boolean }): string;
/** control must be trusted application-authored HTML. */
export declare function field(options: { id: string; label: unknown; control: string; description?: unknown; error?: unknown }): string;
/** markup must come from shared renderers or trusted application code. */
export declare function element(markup: string, ownerDocument?: Document): HTMLElement;
export declare function bindTabs(root: HTMLElement, options?: { onSelect?: (tab: HTMLElement) => void }): () => void;
export declare function bindDialog(dialog: HTMLDialogElement, options?: { initialFocus?: HTMLElement | string; onClose?: (returnValue: string) => void }): { open(invoker?: Element | null): void; close(returnValue?: string): void; destroy(): void };
