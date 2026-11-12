import { createServer } from 'node:http';

const records = new Map();
const queries = [];

createServer(async (request, response) => {
  const url = new URL(request.url, 'http://127.0.0.1:8030');
  const chunks = [];
  for await (const chunk of request) chunks.push(chunk);
  response.setHeader('content-type', 'application/json');
  if (url.pathname === '/records' && request.method === 'POST') {
    const record = JSON.parse(Buffer.concat(chunks));
    if (record.value === null) records.delete(record.name);
    else records.set(record.name, record.value);
    response.end('{}');
  } else if (url.pathname === '/queries' && request.method === 'GET') {
    response.end(JSON.stringify(queries));
  } else if (url.pathname === '/dns-query' && request.method === 'GET' && url.searchParams.get('type') === 'TXT') {
    const name = url.searchParams.get('name');
    queries.push(name);
    const value = records.get(name);
    response.end(JSON.stringify({ Status: 0, Answer: value ? [{ name, type: 16, data: JSON.stringify(value) }] : [] }));
  } else {
    response.statusCode = 404;
    response.end('{}');
  }
}).listen(8030, '127.0.0.1');
