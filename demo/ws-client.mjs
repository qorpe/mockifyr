// WebSocket demo client. Node's browser-style WebSocket cannot set handshake
// headers, so this uses the `ws` package to send X-Mockifyr-Tenant.
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
let WebSocket;
try { WebSocket = require('./node_modules/ws'); }        // local install (demo/node_modules)
catch { WebSocket = require('ws'); }                      // container: NODE_PATH=/deps/node_modules

const ws = new WebSocket(process.env.MOCKIFYR_WS || 'ws://localhost:8080/stream', {
  headers: { 'X-Mockifyr-Tenant': 'acme-pay' },
});

const script = ['ping', 'shout'];
let i = 0;

ws.on('open', () => {
  console.log('connected (tenant acme-pay)');
  const timer = setInterval(() => {
    if (i >= script.length) {
      clearInterval(timer);
      setTimeout(() => ws.close(), 800);
      return;
    }
    console.log('>', script[i]);
    ws.send(script[i++]);
  }, 600);
});
ws.on('message', (data) => console.log('<', data.toString()));
ws.on('close', () => process.exit(0));
ws.on('error', (err) => { console.error(err.message); process.exit(1); });
