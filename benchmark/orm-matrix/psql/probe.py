import os
from pathlib import Path
import re
import subprocess
import sys
from psycopg.conninfo import conninfo_to_dict

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'python'))
from probe import equal, main


class Psql:
    def __init__(self, name):
        self.name = name

    def connect(self):
        self.run('SELECT 1')

    def invoke(self, sql, stop=True, tuples=True):
        parameters = conninfo_to_dict(os.environ['DATABASE_URL'])
        names = {'dbname': 'PGDATABASE', 'host': 'PGHOST', 'hostaddr': 'PGHOSTADDR', 'port': 'PGPORT', 'user': 'PGUSER', 'password': 'PGPASSWORD', 'sslmode': 'PGSSLMODE', 'sslrootcert': 'PGSSLROOTCERT', 'sslcert': 'PGSSLCERT', 'sslkey': 'PGSSLKEY', 'connect_timeout': 'PGCONNECT_TIMEOUT', 'options': 'PGOPTIONS', 'application_name': 'PGAPPNAME'}
        if parameters.keys() - names.keys():
            raise ValueError('unsupported connection parameter names')
        env = {**os.environ, 'PGCONNECT_TIMEOUT': '10', 'PSQL_PAGER': '', **{names[key]: value for key, value in parameters.items()}}
        result = subprocess.run(['psql', '-X', '-qAt' if tuples else '-qA', '-v', f'ON_ERROR_STOP={int(stop)}', '-v', 'VERBOSITY=verbose', '-f', '-'], input=sql + '\n', text=True, capture_output=True, env=env, timeout=45)
        if result.returncode:
            states = re.findall(r'(?:ERROR|FATAL):\s+([0-9A-Z]{5}):', result.stderr)
            raise RuntimeError(f'psql exit={result.returncode} SQLSTATE={states}; {result.stderr[:240]}')
        return result.stdout.strip().splitlines()

    def run(self, sql):
        return [tuple(line.split('|')) for line in self.invoke(sql)]

    def introspect(self, name):
        output = '\n'.join(self.invoke(f'\\dt {name}\n\\d {name}\n\\d {name}_child', tuples=False))
        for expected in (name, 'note', name + '_idx', 'FOREIGN KEY'):
            assert expected in output, f'psql schema description missing {expected}'

    def prepared(self):
        equal(self.run('PREPARE matrix_stmt(integer) AS SELECT $1 + 1; EXECUTE matrix_stmt(41); EXECUTE matrix_stmt(99); DEALLOCATE matrix_stmt'), [('42',), ('100',)])

    def errors(self, cases):
        for statement, expected in cases:
            output = self.invoke(f'BEGIN;\n{statement};\n\\echo :SQLSTATE\nSELECT 1;\n\\echo :SQLSTATE\nROLLBACK;\nSELECT 1;', stop=False)
            equal(output, [expected, '25P02', '1'])

    def close(self):
        pass


if __name__ == '__main__':
    sys.exit(main('psql', Psql))
