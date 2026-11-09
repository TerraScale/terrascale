from __future__ import annotations

from typing import Any, cast

from sqlalchemy import pool
from sqlalchemy.dialects.sqlite.pysqlite import SQLiteDialect_pysqlite
from sqlalchemy.engine.url import URL


class TerraScaleDialect(SQLiteDialect_pysqlite):
    name = "terrascale"
    driver = "terrascale"
    default_isolation_level = "AUTOCOMMIT"
    supports_statement_cache = True

    @classmethod
    def import_dbapi(cls) -> Any:
        from terrascale import dbapi

        return dbapi

    @classmethod
    def get_pool_class(cls, url: URL) -> Any:
        return pool.QueuePool

    def _get_server_version_info(self, connection: Any) -> Any:
        return cast("Any", self.dbapi).sqlite_version_info

    def on_connect(self) -> Any:
        return None

    def create_connect_args(self, url: URL) -> tuple[list[Any], dict[str, Any]]:
        if not url.host or not url.database:
            from sqlalchemy import exc

            raise exc.ArgumentError("Invalid TerraScale URL\nValid form: terrascale://:<api_key>@<project_id>/<database>")
        opts: dict[str, Any] = {"project": url.host, "database": url.database}
        if url.password:
            opts["api_key"] = url.password
        for key in ("base_url", "region"):
            value = url.query.get(key)
            if value:
                opts[key] = value
        return ([], opts)
