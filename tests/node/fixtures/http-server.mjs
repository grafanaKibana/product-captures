import { createServer } from 'node:http';

let input = '';
for await (const chunk of process.stdin) input += chunk;
const { html } = JSON.parse(input);
const server = createServer((request, response) => {
  if (request.url.startsWith('/missing')) {
    response.writeHead(404);
    response.end();
    return;
  }
  response.setHeader('Content-Type', 'text/html');
  response.end(html);
});
server.listen(0, '127.0.0.1', () => {
  console.log(JSON.stringify({ port: server.address().port }));
});
