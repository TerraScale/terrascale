import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { connect } from 'node:http2';
import { stateDir } from './stack';

export async function authRows(sql: string, args: string[]): Promise<unknown[][]> {
  const peer = readFileSync(join(stateDir, 'secrets'), 'utf8').split('\n')[0];
  const connection = connect('http://127.0.0.1:8081');
  try {
    return await new Promise((resolve, reject) => {
      const query = connection.request({ ':method': 'POST',
        ':path': '/v1/projects/00000000000000000000000002/databases/ts-auth/sql/execute',
        'content-type': 'application/json', 'X-TerraBase-Peer-Credential': peer });
      let response = '';
      query.setEncoding('utf8');
      query.on('error', reject);
      query.on('data', chunk => { response += chunk; });
      query.on('end', () => {
        const result = JSON.parse(response);
        if (result.error) reject(new Error(result.error.code));
        else resolve(result.results[0].rows);
      });
      query.end(JSON.stringify({ statements: [{ sql, args }] }));
    });
  } finally {
    connection.close();
  }
}
