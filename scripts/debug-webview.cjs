const WebSocket = require('ws');

const ws = new WebSocket('ws://127.0.0.1:9223/devtools/page/3452A6C980C5FAECC96CF268E8A4878B');

ws.on('open', () => {
  console.log('Connected to CDP');
  ws.send(JSON.stringify({id: 1, method: 'Runtime.enable'}));
  
  // Add a message listener to chrome.webview
  ws.send(JSON.stringify({id: 2, method: 'Runtime.evaluate', params: {expression: `
    chrome.webview.addEventListener('message', (e) => {
      console.log('WEBVIEW MESSAGE:', JSON.stringify(e.data).substring(0, 3000));
    });
    
    // First, discover models for the Ollama configuration
    chrome.webview.postMessage({
      version: 1,
      id: crypto.randomUUID(),
      type: 'byok.discover',
      payload: { configurationId: 'd6704965-70c9-4ee7-bd2b-dcd9b2f72a2d' }
    });
  `, awaitPromise: true, returnByValue: true}}));
  
  setTimeout(() => {
    // Send a chat message in Agent mode
    ws.send(JSON.stringify({id: 3, method: 'Runtime.evaluate', params: {expression: `
      chrome.webview.postMessage({
        version: 1,
        id: crypto.randomUUID(),
        type: 'byok.chat',
        payload: { 
          configurationId: 'd6704965-70c9-4ee7-bd2b-dcd9b2f72a2d',
          text: 'Please delegate to the ollama-coder agent to write a simple hello world function in JavaScript',
          effort: 'Off',
          mode: 'Agent',
          readTools: true,
          writeTools: false,
          terminal: false
        }
      });
    `, awaitPromise: true, returnByValue: true}}));
  }, 5000);
});

ws.on('message', (data) => {
  console.log('CDP Response:', data.toString());
});

ws.on('error', (err) => {
  console.error('Error:', err);
});

setTimeout(() => {
  ws.close();
  process.exit(0);
}, 180000);