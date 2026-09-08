const readline = require('node:readline');
// Pin the Node transport too: new Node versions otherwise select their native WebSocket.
// This supplies a standard constructor without modifying SDK methods or lifecycle state.
globalThis.WebSocket = require('ws');
const { WKIM, WKIMEvent, WKIMChannelType, WKIMDeviceFlag } = require('easyjssdk');

// Use only public SDK APIs; the proxy operates below WebSocket without rewriting frames.
const emit = value => process.stdout.write(`${JSON.stringify(value)}\n`);
const im = WKIM.init(process.env.INTEROP_URL, {
  uid: process.env.INTEROP_UID, token: process.env.INTEROP_TOKEN,
  deviceFlag: WKIMDeviceFlag.Desktop,
}, { singleton: false, debugLogging: false });
im.on(WKIMEvent.Connect, () => emit({ kind: 'connected' }));
im.on(WKIMEvent.Disconnect, () => emit({ kind: 'disconnected' }));
im.on(WKIMEvent.Reconnecting, () => emit({ kind: 'reconnecting' }));
im.on(WKIMEvent.Error, () => emit({ kind: 'error' }));
function sequence(value) {
  if (typeof value === 'number' && !Number.isSafeInteger(value)) throw new Error('Unsafe sequence');
  return String(value);
}
im.on(WKIMEvent.Message, message => emit({
  kind: 'message', messageId: message.messageId, messageSeq: sequence(message.messageSeq),
  clientMsgNo: message.clientMsgNo, fromUid: message.fromUid, payload: message.payload,
}));
(async () => {
  for await (const line of readline.createInterface({ input: process.stdin })) {
    const command = JSON.parse(line);
    try {
      let data = null;
      switch (command.op) {
        case 'connect': await im.connect(); break;
        case 'send': {
          const ack = await im.send(command.target, WKIMChannelType.Person, command.payload,
            { clientMsgNo: command.clientMsgNo });
          data = { messageId: ack.messageId, messageSeq: sequence(ack.messageSeq),
            clientMsgNo: ack.clientMsgNo, reasonCode: ack.reasonCode };
          break;
        }
        case 'disconnect': im.disconnect(); break;
        case 'state': data = { connected: im.isConnected }; break;
        case 'exit': im.destroy(); emit({ kind: 'result', id: command.id, ok: true }); return;
        default: throw new Error('Unsupported operation');
      }
      emit({ kind: 'result', id: command.id, ok: true, data });
    } catch (error) {
      emit({ kind: 'result', id: command.id, ok: false, category: 'SDK rejection',
        code: Number.isInteger(error?.code) ? error.code : null });
    }
  }
})().finally(() => im.destroy()).catch(() => { process.exitCode = 1; });
