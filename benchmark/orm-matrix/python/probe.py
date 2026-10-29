import json
import logging
import os
import re
import sys
from ulid import ULID

logging.disable(logging.CRITICAL)


def cause(error):
    original = getattr(error, 'orig', error)
    state = getattr(original, 'sqlstate', None)
    message = str(original).splitlines()[0] if str(original) else type(original).__name__
    message = re.sub(r'(?i)postgres(?:ql)?(?:\+\w+)?://\S+|(?:password|user|host|dbname)=\S+', '[redacted]', message)
    from urllib.parse import unquote, urlsplit
    url = os.environ['DATABASE_URL']
    for secret in (url, urlsplit(url).password, urlsplit(url).username):
        if secret:
            message = message.replace(secret, '[redacted]').replace(unquote(secret), '[redacted]')
    return f'{type(original).__name__} SQLSTATE={state or "unknown"}: {message[:240]}'


def equal(actual, expected):
    if actual != expected:
        raise AssertionError(f'expected synthetic result {expected!r}, got {actual!r}')


class SQLAlchemy:
    def __init__(self, name):
        from sqlalchemy import Column, ForeignKey, MetaData, String, create_engine
        from sqlalchemy.dialects.postgresql import JSONB
        from sqlalchemy.engine import make_url
        from sqlalchemy.orm import registry
        url = make_url(os.environ['DATABASE_URL']).set(drivername='postgresql+psycopg')
        self.engine = create_engine(url, connect_args={'connect_timeout': 10}, hide_parameters=True)
        self.conn = None
        self.metadata = MetaData()
        mapper = registry(metadata=self.metadata)
        from sqlalchemy import Table
        parent = Table(name, self.metadata, Column('id', String(26), primary_key=True), Column('label', String, unique=True), Column('payload', JSONB))
        self.child = Table(name + '_child', self.metadata, Column('id', String(26), primary_key=True), Column('parent_id', String(26), ForeignKey(parent.c.id)))
        self.model = type('Record', (), {})
        mapper.map_imperatively(self.model, parent)

    def connect(self):
        self.conn = self.engine.connect().execution_options(isolation_level='AUTOCOMMIT')

    def run(self, sql):
        rows = []
        for statement in sql.split(';'):
            if statement.strip():
                result = self.conn.exec_driver_sql(statement)
                if result.returns_rows:
                    rows.extend(tuple(str(v) for v in row) for row in result)
        return rows

    def table(self, name):
        self.metadata.create_all(self.engine, tables=[self.metadata.tables[name]])
        self.run(f'ALTER TABLE {name} ADD COLUMN note TEXT')

    def fk(self, name):
        self.metadata.create_all(self.engine, tables=[self.child])

    def introspect(self, name):
        from sqlalchemy import MetaData, inspect
        metadata = MetaData()
        metadata.reflect(self.engine, only=[name, name + '_child'])
        equal(set(metadata.tables[name].c.keys()), {'id', 'label', 'payload', 'note'})
        inspector = inspect(self.engine)
        equal(inspector.get_foreign_keys(name + '_child')[0]['referred_table'], name)
        assert any(i['name'] == name + '_idx' for i in inspector.get_indexes(name))

    def crud(self, name, key):
        from sqlalchemy.orm import Session
        with Session(self.engine) as session:
            row = self.model(id=key, label='first', payload={'answer': 42})
            session.add(row)
            session.commit()
            equal(session.get(self.model, key).label, 'first')
            row.label = 'updated'
            session.commit()
            session.expire_all()
            equal(session.get(self.model, key).label, 'updated')
            row.label = 'rolled_back'
            session.rollback()
            equal(session.get(self.model, key).label, 'updated')
            session.delete(row)
            session.commit()
            equal(session.get(self.model, key), None)

    def prepared(self):
        raw = self.conn.connection.driver_connection
        for value in (41, 99):
            equal(raw.execute('SELECT %s::integer + 1', (value,), prepare=True).fetchone(), (value + 1,))

    def close(self):
        if self.conn:
            self.conn.close()
        self.engine.dispose()


class Pgcli:
    def __init__(self, name):
        self.client = None

    def connect(self):
        from pgcli.pgexecute import PGExecute
        self.client = PGExecute(dsn=os.environ['DATABASE_URL'], connect_timeout=10)

    def run(self, sql):
        rows = []
        for result in self.client.run(sql):
            assert result[5], 'PGExecute returned an unsuccessful result'
            if result[1] is not None:
                rows.extend(tuple(str(v) for v in row) for row in result[1])
        return rows

    def introspect(self, name):
        assert any(row[1] == name for row in self.client.tables())
        assert any(row[1:3] == (name, 'note') for row in self.client.table_columns())
        assert list(self.client.schemata())
        assert any(row.childtable == name + '_child' for row in self.client.foreignkeys())

    def prepared(self):
        equal(self.run('PREPARE matrix_stmt(integer) AS SELECT $1 + 1; EXECUTE matrix_stmt(41); EXECUTE matrix_stmt(99); DEALLOCATE matrix_stmt'), [('42',), ('100',)])

    def close(self):
        if self.client:
            self.client.conn.close()


def main(tool, factory):
    failures = 0
    repeats = int(os.environ.get('MATRIX_REPEAT', '3'))
    if repeats < 1 or not os.environ['MATRIX_SURFACE']:
        raise ValueError('MATRIX_REPEAT must be positive and MATRIX_SURFACE must be nonempty')
    for repeat in range(1, repeats + 1):
        name, key = 'matrix_' + str(ULID()).lower(), str(ULID())
        client, states, details = factory(name), {}, {}

        def check(label, action, dependencies=()):
            nonlocal failures
            blocked = next((d for d in dependencies if states.get(d) != 'pass'), None)
            try:
                if blocked:
                    states[label], details[label] = 'blocked', f'{blocked}: {details[blocked]}'
                else:
                    action()
                    states[label], details[label] = 'pass', 'assertions passed'
            except Exception as error:
                states[label], details[label] = 'fail', cause(error)
                try:
                    client.run('ROLLBACK')
                except Exception:
                    pass
            failures += states[label] != 'pass'
            print(json.dumps(dict(surface=os.environ['MATRIX_SURFACE'], tool=tool, repeat=repeat, check=label, status=states[label], detail=details[label])), flush=True)

        def connected():
            client.connect()
            equal(client.run('SELECT 1'), [('1',)])

        def errors():
            cases = [(f"INSERT INTO {name}(id,label) VALUES ('{ULID()}', 'seed')", '23505'), (f"INSERT INTO {name}_child(id,parent_id) VALUES ('{ULID()}', '{ULID()}')", '23503'), ('SELEC 1', '42601')]
            if hasattr(client, 'errors'):
                client.errors(cases)
                return
            for statement, expected in cases:
                client.run('BEGIN')
                try:
                    client.run(statement)
                except Exception as error:
                    equal(getattr(getattr(error, 'orig', error), 'sqlstate', None), expected)
                else:
                    raise AssertionError(f'missing SQLSTATE {expected}')
                try:
                    client.run('SELECT 1')
                except Exception as error:
                    equal(getattr(getattr(error, 'orig', error), 'sqlstate', None), '25P02')
                else:
                    raise AssertionError('missing failed-transaction SQLSTATE 25P02')
                client.run('ROLLBACK')
                equal(client.run('SELECT 1'), [('1',)])

        try:
            check('connect', connected)
            check('migrate_table', lambda: client.table(name) if hasattr(client, 'table') else client.run(f'CREATE TABLE {name}(id VARCHAR(26) PRIMARY KEY, label TEXT UNIQUE, payload JSONB); ALTER TABLE {name} ADD COLUMN note TEXT'), ['connect'])
            check('migrate_fk', lambda: client.fk(name) if hasattr(client, 'fk') else client.run(f'CREATE TABLE {name}_child(id VARCHAR(26) PRIMARY KEY, parent_id VARCHAR(26) REFERENCES {name}(id))'), ['migrate_table'])
            check('migrate_index', lambda: client.run(f'CREATE INDEX {name}_idx ON {name}(note)'), ['migrate_table'])
            check('introspect', lambda: client.introspect(name), ['migrate_fk', 'migrate_index'])
            check('crud', lambda: client.crud(name, key) if hasattr(client, 'crud') else equal(client.run(f"INSERT INTO {name}(id,label) VALUES ('{key}','first') RETURNING label; SELECT label FROM {name} WHERE id='{key}'; UPDATE {name} SET label='updated' WHERE id='{key}' RETURNING label; DELETE FROM {name} WHERE id='{key}' RETURNING label; SELECT COUNT(*) FROM {name}"), [('first',), ('first',), ('updated',), ('updated',), ('0',)]), ['migrate_table'])
            check('transactions', lambda: equal(client.run(f"BEGIN; INSERT INTO {name}(id,label) VALUES ('{key}','seed'); COMMIT; BEGIN; UPDATE {name} SET label='discard'; ROLLBACK; SELECT label FROM {name} WHERE id='{key}'"), [('seed',)]), ['migrate_table', 'crud'])
            check('json', lambda: equal(client.run(f"UPDATE {name} SET payload='{{\"answer\":42}}'::jsonb WHERE id='{key}'; SELECT payload->>'answer' FROM {name} WHERE id='{key}'"), [('42',)]), ['transactions'])
            check('prepared', client.prepared, ['connect'])
            check('errors', errors, ['transactions', 'migrate_fk'])
        finally:
            if states.get('connect') == 'pass':
                check('cleanup', lambda: client.run(f'DROP TABLE IF EXISTS {name}_child; DROP TABLE IF EXISTS {name}'))
            check('cleanup_connection', client.close)
    return int(failures > 0)


if __name__ == '__main__':
    tool = sys.argv[1]
    sys.exit(main(tool, {'sqlalchemy': SQLAlchemy, 'pgcli': Pgcli}[tool]))
