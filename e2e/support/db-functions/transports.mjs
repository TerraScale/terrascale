import { once } from 'node:events';
import { lookup } from 'node:dns/promises';

export async function token(key, project) {
  const id = key.replace(/^sk_/, '').slice(0, 26);
  const response = await fetch('https://auth.terrascale.tech/terrascale/oauth/token', {
    method: 'POST', headers: { authorization: 'Basic ' + Buffer.from(id + ':' + key).toString('base64') },
    body: new URLSearchParams({ grant_type: 'client_credentials', project }), signal: AbortSignal.timeout(15000),
  });
  if (!response.ok) throw new Error('token exchange HTTP ' + response.status);
  const body = await response.json();
  if (typeof body.access_token !== 'string') throw new Error('token exchange response');
  return body.access_token;
}

export async function transport(surface, region, project, database, credential, ports) {
  const origin = 'https://' + region + '.api.terrascale.tech';
  const path = '/v1/projects/' + project + '/databases/' + database + '/sql/';
  if (surface === 'REST') return {
    execute: async statements => {
      const response = await fetch(origin + path + 'execute', {
        method: 'POST', headers: { authorization: 'Bearer ' + credential.token, 'content-type': 'application/json', 'TerraScale-Write': 'home' },
        body: JSON.stringify({ statements }), signal: AbortSignal.timeout(30000), redirect: 'error',
      });
      const body = await response.json();
      if (body.error) throw Object.assign(new Error('SQL response'), body.error, { sqlstate: body.error.sqlState ?? body.error.sqlstate, status: response.status });
      if (!response.ok || !Array.isArray(body.results)) throw new Error('SQL response HTTP ' + response.status);
      return Object.assign(body.results, { region: response.headers.get('x-terrabase-region'), route: response.headers.get('terrascale-write-route') });
    },
    close: async () => {},
  };
  if (surface === 'WebSocket') {
    const socket = new WebSocket(origin.replace('https:', 'wss:') + path + 'ws', 'terrascale.sql.v2');
    let id = 0;
    const pending = new Map();
    socket.addEventListener('message', event => {
      const body = JSON.parse(String(event.data));
      const waiting = pending.get(body.id);
      if (!waiting) return;
      pending.delete(body.id);
      if (body.error) waiting.reject(Object.assign(new Error('SQL frame'), body.error, { sqlstate: body.error.sqlState ?? body.error.sqlstate }));
      else waiting.resolve(body.results);
    });
    socket.addEventListener('close', event => {
      for (const waiting of pending.values()) waiting.reject(Object.assign(new Error('SQL socket closed'), { closeCode: event.code }));
      pending.clear();
    });
    await once(socket, 'open', { signal: AbortSignal.timeout(15000) });
    const send = (type, fields = {}) => {
      const sequence = ++id;
      return new Promise((resolve, reject) => {
        const deadline = setTimeout(() => { pending.delete(sequence); reject(new Error('SQL frame deadline')); socket.close(); }, 30000);
        pending.set(sequence, {
          resolve: value => { clearTimeout(deadline); resolve(value); },
          reject: error => { clearTimeout(deadline); reject(error); },
        });
        socket.send(JSON.stringify({ id: sequence, type, ...fields }));
      });
    };
    try {
      await send('auth', { token: credential.token });
    } catch (error) {
      socket.close();
      throw error;
    }
    return { execute: statements => send('execute', { statements }), control: (type, fields) => send(type, fields), close: async () => { socket.close(); } };
  }
  const { default: pg } = await import('pg');
  const address = ports ? { address: '127.0.0.1' } : await lookup(region + '.api.terrascale.tech', { family: 4 });
  const client = new pg.Client({ host: address.address, port: ports ? ports[region === 'gru' ? 'postgres' : 'postgres_fra'] : 5432, database: project + '/' + database,
    user: 'any', password: credential.apiKey, ssl: ports ? false : { servername: 'pg.terrascale.tech', rejectUnauthorized: true },
    connectionTimeoutMillis: 15000, query_timeout: 30000 });
  let broken;
  client.on('error', error => { broken = error; });
  await client.connect();
  const execute = async statements => {
    const results = [];
    for (const statement of statements) {
      if (broken) throw Object.assign(new Error(broken.message), { sqlstate: broken.code });
      let sql = statement.sql;
      let values;
      if (statement.named) {
        const names = [];
        sql = sql.replace(/:([a-z]+)/g, (_, name) => { names.push(name); return '$' + names.length; });
        values = names.map(name => statement.named[name]);
      } else if (statement.args) {
        let n = 0;
        sql = sql.replace(/\?/g, () => '$' + ++n);
        values = statement.args.map(value => value?.base64 ? Buffer.from(value.base64, 'base64') : value);
      }
      try {
        const types = statement.oids ? Object.assign([...statement.oids], { getTypeParser: pg.types.getTypeParser }) : undefined;
        const result = await client.query({ text: sql, values, types, rowMode: 'array' });
        for (const part of Array.isArray(result) ? result : [result]) results.push({ rows: part.rows });
      } catch (error) {
        throw Object.assign(new Error(error.message), { sqlstate: error.code });
      }
    }
    return results;
  };
  const describeBind = (name, text, values) => new Promise((resolve, reject) => {
    let oids;
    const parameters = message => { oids = message.dataTypeIDs; };
    const query = new pg.Query({ name, text, values, types: [0], rowMode: 'array' }, undefined, (error, result) => {
      client.connection.removeListener('parameterDescription', parameters);
      if (error) reject(Object.assign(new Error(error.message), { sqlstate: error.code }));
      else resolve({ oids, rows: result.rows });
    });
    query.prepare = connection => {
      if (!query.hasBeenParsed(connection)) {
        connection.parse({ name, text, types: [0] });
        connection.submittedNamedStatements[name] = text;
      }
      connection.describe({ type: 'S', name });
      connection.bind({ statement: name, values });
      connection.execute({ portal: '', rows: 0 });
      connection.sync();
    };
    client.connection.on('parameterDescription', parameters);
    client.query(query);
  });
  return {
    execute,
    describeBind,
    control: (type, fields) => execute([{ sql: type === 'begin' && fields?.consistency === 'strong' ? 'BEGIN ISOLATION LEVEL SERIALIZABLE' : type.toUpperCase() }]),
    close: async () => { await client.end(); if (broken) throw Object.assign(new Error(broken.message), { sqlstate: broken.code }); },
    address: address.address,
  };
}
