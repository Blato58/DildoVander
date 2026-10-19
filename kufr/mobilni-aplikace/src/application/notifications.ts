import { readonly, ref } from 'vue';

const notices = ref<{ id: number; message: string; tone: 'info' | 'danger' }[]>([]);
let nextId = 0;
export const notifications = readonly(notices);
export function notify(message: string, tone: 'info' | 'danger' = 'info'): void {
  notices.value = [...notices.value.slice(-3), { id: ++nextId, message, tone }];
}
export function dismissNotice(id: number): void {
  notices.value = notices.value.filter(notice => notice.id !== id);
}
