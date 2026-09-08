import { WKIM, WKIMEvent, WKIMChannelType, WKIMDeviceFlag } from 'easyjssdk';

// The page runs the SDK with the browser's unmodified native WebSocket.
window.startInterop = ({ url, uid, token }) => {
  const emit = value => { void window.reportInterop(value); };
  const im = WKIM.init(url, { uid, token, deviceFlag: WKIMDeviceFlag.Desktop },
    { singleton: false, debugLogging: false });
  const state = document.querySelector('#state');
  im.on(WKIMEvent.Connect, () => { state.textContent = 'Connected'; emit({ kind: 'connected' }); });
  im.on(WKIMEvent.Disconnect, () => { state.textContent = 'Disconnected'; emit({ kind: 'disconnected' }); });
  im.on(WKIMEvent.Reconnecting, () => emit({ kind: 'reconnecting' }));
  im.on(WKIMEvent.Error, () => emit({ kind: 'error' }));
  function sequence(value) {
    if (typeof value === 'number' && !Number.isSafeInteger(value)) throw new Error('Unsafe sequence');
    return String(value);
  }
  im.on(WKIMEvent.Message, message => emit({ kind: 'message', messageId: message.messageId,
    messageSeq: sequence(message.messageSeq), clientMsgNo: message.clientMsgNo,
    fromUid: message.fromUid, payload: message.payload }));
  window.interopCommand = async command => {
    try {
      let data = null;
      switch (command.op) {
        case 'connect': await im.connect(); break;
        case 'disconnect': im.disconnect(); break;
        case 'state': data = { connected: im.isConnected }; break;
        case 'exit': im.destroy(); break;
        case 'send': {
          const ack = await im.send(command.target, WKIMChannelType.Person, command.payload,
            { clientMsgNo: command.clientMsgNo });
          data = { messageId: ack.messageId, messageSeq: sequence(ack.messageSeq),
            clientMsgNo: ack.clientMsgNo, reasonCode: ack.reasonCode };
          break;
        }
        default: throw new Error('Unsupported fixture command');
      }
      return { kind: 'result', id: command.id, ok: true, data };
    } catch (error) {
      return { kind: 'result', id: command.id, ok: false, category: 'SDK rejection',
        code: Number.isInteger(error?.code) ? error.code : null };
    }
  };
};
