import { Capacitor, registerPlugin } from '@capacitor/core'

export interface CustomerBleDevice { id: string; name: string }
export interface CustomerBleStatus {
  v: 1
  deviceId: string
  bootId: string
  challenge: string
  capabilities: { customerStart: 0 | 1 }
  state: 'idle' | 'starting' | 'running' | 'recovering' | 'completed' | 'failed'
  operationId?: string
  sessionId?: string
  error?: string
}
interface CustomerBleNative {
  discover(): Promise<{ devices: CustomerBleDevice[] }>
  connect(options: { id: string }): Promise<CustomerBleStatus>
  start(options: { ticket: string; operationId: string }): Promise<CustomerBleStatus>
  status(options: { operationId?: string }): Promise<CustomerBleStatus>
  end(options: { sessionId: string; operationId?: string }): Promise<CustomerBleStatus>
  disconnect(): Promise<void>
}
const native = registerPlugin<CustomerBleNative>('KufrCustomerBle')
let generation = 0
let operationId: string | undefined
async function current<T>(request: () => Promise<T>): Promise<T> {
  if (!customerBle.supported()) throw new Error('customer_ble_unsupported')
  const epoch = generation
  const result = await request()
  if (epoch !== generation) throw new Error('cancelled')
  return result
}
export const customerBle = {
  supported: () => Capacitor.isNativePlatform() && Capacitor.getPlatform() === 'ios',
  discover: () => current(() => native.discover()),
  connect: (id: string) => current(() => native.connect({ id })),
  start: (ticket: string, id: string) => {
    operationId = id
    return current(() => native.start({ ticket, operationId: id }))
  },
  status: () => current(() => native.status({ operationId })),
  // Account HTTP end authorizes this action. BLE only prompts status reconciliation.
  end: (sessionId: string) => current(() => native.end({ sessionId, operationId })),
  disconnect: async () => {
    generation += 1
    operationId = undefined
    if (customerBle.supported()) await native.disconnect()
  },
}
