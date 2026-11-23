CREATE TABLE IF NOT EXISTS topology (
  id INTEGER PRIMARY KEY CHECK (id = 1),
  updated INTEGER NOT NULL,
  doc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS placements (
  project TEXT NOT NULL,
  database TEXT NOT NULL,
  region TEXT,
  updated INTEGER NOT NULL,
  PRIMARY KEY (project, database)
);
