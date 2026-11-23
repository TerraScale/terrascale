using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal static class PgCatalogSql
{
    public const int FirstObjectOid = 16384;
    private const string DatabaseToken = "\u0001db\u0001";
    private const string UserToken = "\u0001user\u0001";

    private static readonly string TypeOid = BuildTypeOid("c_decl");
    private static readonly string TypeName = BuildCase("c._tb_typid", static type => type.Name, "text");
    private static readonly string FormatBase = BuildCase("c._tb_typid", static type => type.FormatName, "text");
    private static readonly string TypeLength = BuildCase("c._tb_typid", static type => type.Length.ToString(CultureInfo.InvariantCulture), "-1", quote: false);

    private static readonly string Columns = $$"""
        SELECT m.rowid AS _tb_relrowid, m.name AS _tb_relname, m.type AS _tb_reltype, p.cid AS _tb_cid, p.name AS _tb_attname, p.type AS _tb_decl,
            p."notnull" AS _tb_notnull, p.dflt_value AS _tb_default, p.pk AS _tb_pk, {{TypeOid.Replace("c_decl", "p.type", StringComparison.Ordinal)}} AS _tb_typid,
            (SELECT count(*) FROM pragma_table_info(m.name) q WHERE q.pk > 0) AS _tb_pkcount
        FROM sqlite_schema m JOIN pragma_table_info(m.name) p
        WHERE m.type IN ('table', 'view') AND {{UserObject("m")}}
        """;

    private static readonly string Identity = "c._tb_pk = 1 AND c._tb_pkcount = 1 AND upper(c._tb_decl) = 'INTEGER'";

    private static readonly string Format = $"({FormatBase} || CASE WHEN c._tb_typid IN (1042, 1043, 1700) AND instr(c._tb_decl, '(') > 0 THEN lower(substr(c._tb_decl, instr(c._tb_decl, '('))) ELSE '' END)";

    private static readonly string PgAttribute = $"""
        SELECT CAST({FirstObjectOid} + c._tb_relrowid AS INTEGER) AS attrelid, c._tb_attname AS attname, CAST(c._tb_typid AS INTEGER) AS atttypid,
            CAST(-1 AS INTEGER) AS attstattarget, CAST({TypeLength} AS INTEGER) AS attlen, CAST(c._tb_cid + 1 AS INTEGER) AS attnum, CAST(0 AS INTEGER) AS attndims,
            CAST(-1 AS INTEGER) AS attcacheoff,
            CAST(CASE WHEN c._tb_typid IN (1042, 1043) AND instr(c._tb_decl, '(') > 0 THEN CAST(substr(c._tb_decl, instr(c._tb_decl, '(') + 1) AS INTEGER) + 4 ELSE -1 END AS INTEGER) AS atttypmod,
            CAST(0 AS INTEGER) AS attbyval, 'x' AS attstorage, 'i' AS attalign, CAST(c._tb_notnull OR c._tb_pk > 0 AS INTEGER) AS attnotnull,
            CAST(c._tb_default IS NOT NULL AS INTEGER) AS atthasdef, CAST(0 AS INTEGER) AS atthasmissing, CASE WHEN {Identity} THEN 'd' ELSE '' END AS attidentity,
            '' AS attgenerated, CAST(0 AS INTEGER) AS attisdropped, CAST(1 AS INTEGER) AS attislocal, CAST(0 AS INTEGER) AS attinhcount, CAST(0 AS INTEGER) AS attcollation,
            NULL AS attacl, NULL AS attoptions, NULL AS attfdwoptions, NULL AS attmissingval, '' AS attcompression, {Format} AS _tb_format, {DefaultExpression("c._tb_default")} AS _tb_default
        FROM ({Columns}) c
        """;

    private static readonly string PgAttrdef = $"""
        SELECT CAST(5000000 + (c._tb_relrowid * 1000) + c._tb_cid AS INTEGER) AS oid, CAST({FirstObjectOid} + c._tb_relrowid AS INTEGER) AS adrelid,
            CAST(c._tb_cid + 1 AS INTEGER) AS adnum, {DefaultExpression("c._tb_default")} AS adbin
        FROM ({Columns}) c WHERE c._tb_default IS NOT NULL
        """;

    private static readonly string PgClass = $"""
        SELECT CAST(u.oid AS INTEGER) AS oid, u.relname AS relname, CAST(2200 AS INTEGER) AS relnamespace, CAST(0 AS INTEGER) AS reltype, CAST(0 AS INTEGER) AS reloftype,
            CAST(10 AS INTEGER) AS relowner, CAST(u.relam AS INTEGER) AS relam, CAST(u.oid AS INTEGER) AS relfilenode, CAST(0 AS INTEGER) AS reltablespace,
            CAST(0 AS INTEGER) AS relpages, CAST(-1 AS REAL) AS reltuples, CAST(0 AS INTEGER) AS relallvisible, CAST(0 AS INTEGER) AS reltoastrelid,
            CAST(u.relhasindex AS INTEGER) AS relhasindex, CAST(0 AS INTEGER) AS relisshared, 'p' AS relpersistence, u.relkind AS relkind,
            CAST(u.relnatts AS INTEGER) AS relnatts, CAST(0 AS INTEGER) AS relchecks, CAST(0 AS INTEGER) AS relhasrules, CAST(u.relhastriggers AS INTEGER) AS relhastriggers,
            CAST(0 AS INTEGER) AS relhassubclass, CAST(0 AS INTEGER) AS relrowsecurity, CAST(0 AS INTEGER) AS relforcerowsecurity, CAST(1 AS INTEGER) AS relispopulated,
            'd' AS relreplident, CAST(0 AS INTEGER) AS relispartition, CAST(0 AS INTEGER) AS relrewrite, CAST(0 AS INTEGER) AS relfrozenxid, CAST(0 AS INTEGER) AS relminmxid,
            NULL AS relacl, NULL AS reloptions, NULL AS relpartbound, u.sql AS _tb_sql
        FROM (
            SELECT {FirstObjectOid} + o.rowid AS oid, CASE WHEN o.type = 'index' THEN {IndexName("o.name", "o.tbl_name", "(SELECT il.origin FROM pragma_index_list(o.tbl_name) il WHERE il.name = o.name)")} ELSE o.name END AS relname, CASE o.type WHEN 'table' THEN 2 WHEN 'index' THEN 403 ELSE 0 END AS relam,
                o.type = 'table' AND (EXISTS (SELECT 1 FROM pragma_index_list(o.name)) OR EXISTS (SELECT 1 FROM pragma_table_info(o.name) WHERE pk > 0)) AS relhasindex,
                CASE o.type WHEN 'table' THEN 'r' WHEN 'view' THEN 'v' ELSE 'i' END AS relkind,
                CASE WHEN o.type = 'index' THEN (SELECT count(*) FROM pragma_index_info(o.name)) ELSE (SELECT count(*) FROM pragma_table_info(o.name)) END AS relnatts,
                EXISTS (SELECT 1 FROM sqlite_schema t WHERE t.type = 'trigger' AND t.tbl_name = o.name) AS relhastriggers, o.sql AS sql
            FROM sqlite_schema o WHERE o.type IN ('table', 'view', 'index') AND {UserObject("o")}
            UNION ALL
            SELECT 9000000 + t.rowid, t.name || '_pkey', 403, 0, 'i', (SELECT count(*) FROM pragma_table_info(t.name) WHERE pk > 0), 0, NULL
            FROM sqlite_schema t WHERE {RowidPrimaryKey("t")}
        ) u
        """;

    private static readonly string PgIndex = $"""
        SELECT CAST(u.indexrelid AS INTEGER) AS indexrelid, CAST(u.indrelid AS INTEGER) AS indrelid, CAST(u.indnatts AS INTEGER) AS indnatts,
            CAST(u.indnatts AS INTEGER) AS indnkeyatts, CAST(u.indisunique AS INTEGER) AS indisunique, CAST(0 AS INTEGER) AS indnullsnotdistinct,
            CAST(u.indisprimary AS INTEGER) AS indisprimary, CAST(0 AS INTEGER) AS indisexclusion, CAST(1 AS INTEGER) AS indimmediate, CAST(0 AS INTEGER) AS indisclustered,
            CAST(1 AS INTEGER) AS indisvalid, CAST(0 AS INTEGER) AS indcheckxmin, CAST(1 AS INTEGER) AS indisready, CAST(1 AS INTEGER) AS indislive,
            CAST(0 AS INTEGER) AS indisreplident, u.indkey AS indkey, '' AS indcollation, '' AS indclass, '' AS indoption, NULL AS indexprs, u.indpred AS indpred,
            u.indexdef AS _tb_indexdef, u.indexname AS _tb_indexname, u.tablename AS _tb_tablename
        FROM (
            SELECT {FirstObjectOid} + i.rowid AS indexrelid, {FirstObjectOid} + t.rowid AS indrelid, (SELECT count(*) FROM pragma_index_info(il.name)) AS indnatts,
                il."unique" AS indisunique, il.origin = 'pk' AS indisprimary, (SELECT group_concat(ii.cid + 1, ' ') FROM pragma_index_info(il.name) ii) AS indkey,
                CASE WHEN il.partial THEN 'true' ELSE NULL END AS indpred,
                'CREATE ' || CASE WHEN il."unique" THEN 'UNIQUE ' ELSE '' END || 'INDEX ' || {IndexName("il.name", "t.name", "il.origin")} || ' ON public.' || t.name || ' USING btree ('
                    || coalesce((SELECT group_concat(ii.name, ', ') FROM pragma_index_info(il.name) ii), '') || ')' AS indexdef,
                {IndexName("il.name", "t.name", "il.origin")} AS indexname, t.name AS tablename
            FROM sqlite_schema t JOIN pragma_index_list(t.name) il JOIN sqlite_schema i ON i.name = il.name AND i.type = 'index'
            WHERE t.type = 'table' AND {UserObject("t")}
            UNION ALL
            SELECT 9000000 + t.rowid, {FirstObjectOid} + t.rowid, (SELECT count(*) FROM pragma_table_info(t.name) WHERE pk > 0), 1, 1,
                (SELECT group_concat(cid + 1, ' ') FROM pragma_table_info(t.name) WHERE pk > 0), NULL,
                'CREATE UNIQUE INDEX ' || t.name || '_pkey ON public.' || t.name || ' USING btree ('
                    || (SELECT group_concat(name, ', ') FROM pragma_table_info(t.name) WHERE pk > 0) || ')',
                t.name || '_pkey', t.name
            FROM sqlite_schema t WHERE {RowidPrimaryKey("t")}
        ) u
        """;

    private static readonly string ForeignKeyName = "t.name || '_' || (SELECT group_concat(g.\"from\", '_') FROM pragma_foreign_key_list(t.name) g WHERE g.id = f.id) || '_fkey'";

    private static readonly string PgConstraint = $$"""
        SELECT CAST(u.oid AS INTEGER) AS oid, u.conname AS conname, CAST(2200 AS INTEGER) AS connamespace, u.contype AS contype, CAST(0 AS INTEGER) AS condeferrable,
            CAST(0 AS INTEGER) AS condeferred, CAST(1 AS INTEGER) AS convalidated, CAST(u.conrelid AS INTEGER) AS conrelid, CAST(0 AS INTEGER) AS contypid,
            CAST(u.conindid AS INTEGER) AS conindid, CAST(0 AS INTEGER) AS conparentid, CAST(u.confrelid AS INTEGER) AS confrelid, u.confupdtype AS confupdtype,
            u.confdeltype AS confdeltype, u.confmatchtype AS confmatchtype, CAST(1 AS INTEGER) AS conislocal, CAST(0 AS INTEGER) AS coninhcount,
            CAST(1 AS INTEGER) AS connoinherit, CAST(0 AS INTEGER) AS conperiod, u.conkey AS conkey, u.confkey AS confkey, NULL AS conpfeqop, NULL AS conppeqop,
            NULL AS conffeqop, NULL AS confdelsetcols, NULL AS conexclop, NULL AS conbin, u.def AS _tb_def, u.tablename AS _tb_tablename, u.reftable AS _tb_reftable
        FROM (
            SELECT 7000000 + t.rowid AS oid, t.name || '_pkey' AS conname, 'p' AS contype, {{FirstObjectOid}} + t.rowid AS conrelid,
                coalesce((SELECT {{FirstObjectOid}} + i.rowid FROM pragma_index_list(t.name) il JOIN sqlite_schema i ON i.name = il.name WHERE il.origin = 'pk'), 9000000 + t.rowid) AS conindid,
                0 AS confrelid, ' ' AS confupdtype, ' ' AS confdeltype, ' ' AS confmatchtype,
                '{' || (SELECT group_concat(cid + 1, ',') FROM pragma_table_info(t.name) WHERE pk > 0) || '}' AS conkey, NULL AS confkey,
                'PRIMARY KEY (' || (SELECT group_concat(name, ', ') FROM pragma_table_info(t.name) WHERE pk > 0) || ')' AS def, t.name AS tablename, NULL AS reftable
            FROM sqlite_schema t WHERE t.type = 'table' AND {{UserObject("t")}} AND EXISTS (SELECT 1 FROM pragma_table_info(t.name) WHERE pk > 0)
            UNION ALL
            SELECT 6000000 + i.rowid, {{IndexName("il.name", "t.name", "il.origin")}}, 'u', {{FirstObjectOid}} + t.rowid, {{FirstObjectOid}} + i.rowid, 0, ' ', ' ', ' ',
                '{' || (SELECT group_concat(ii.cid + 1, ',') FROM pragma_index_info(il.name) ii) || '}', NULL,
                'UNIQUE (' || (SELECT group_concat(ii.name, ', ') FROM pragma_index_info(il.name) ii) || ')', t.name, NULL
            FROM sqlite_schema t JOIN pragma_index_list(t.name) il JOIN sqlite_schema i ON i.name = il.name
            WHERE t.type = 'table' AND {{UserObject("t")}} AND il.origin = 'u'
            UNION ALL
            SELECT 8000000 + (t.rowid * 100) + f.id, {{ForeignKeyName}}, 'f', {{FirstObjectOid}} + t.rowid, 0,
                coalesce((SELECT {{FirstObjectOid}} + r.rowid FROM sqlite_schema r WHERE r.name = f."table" AND r.type = 'table'), 0),
                {{Action("f.on_update")}}, {{Action("f.on_delete")}}, 's',
                '{' || group_concat((SELECT c.cid + 1 FROM pragma_table_info(t.name) c WHERE c.name = f."from"), ',') || '}',
                '{' || group_concat(coalesce((SELECT c.cid + 1 FROM pragma_table_info(f."table") c WHERE c.name = f."to"), 0), ',') || '}',
                'FOREIGN KEY (' || group_concat(f."from", ', ') || ') REFERENCES ' || f."table" || '(' || group_concat(coalesce(f."to", ''), ', ') || ')'
                    || CASE WHEN f.on_update <> 'NO ACTION' THEN ' ON UPDATE ' || f.on_update ELSE '' END
                    || CASE WHEN f.on_delete <> 'NO ACTION' THEN ' ON DELETE ' || f.on_delete ELSE '' END,
                t.name, f."table"
            FROM sqlite_schema t JOIN pragma_foreign_key_list(t.name) f WHERE t.type = 'table' AND {{UserObject("t")}} GROUP BY t.rowid, f.id
        ) u
        """;

    private static readonly string PgType = BuildPgType();

    private static readonly FrozenDictionary<string, string[]> EmptyRelations = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["pg_proc"] = ["oid", "proname", "pronamespace", "proowner", "prolang", "procost", "prorows", "provariadic", "prosupport", "prokind", "prosecdef", "proleakproof", "proisstrict", "proretset", "provolatile", "proparallel", "pronargs", "pronargdefaults", "prorettype", "proargtypes", "proallargtypes", "proargmodes", "proargnames", "proargdefaults", "protrftypes", "prosrc", "probin", "prosqlbody", "proconfig", "proacl"],
        ["pg_description"] = ["objoid", "classoid", "objsubid", "description"],
        ["pg_shdescription"] = ["objoid", "classoid", "description"],
        ["pg_inherits"] = ["inhrelid", "inhparent", "inhseqno", "inhdetachpending"],
        ["pg_trigger"] = ["oid", "tgrelid", "tgparentid", "tgname", "tgfoid", "tgtype", "tgenabled", "tgisinternal", "tgconstrrelid", "tgconstrindid", "tgconstraint", "tgdeferrable", "tginitdeferred", "tgnargs", "tgattr", "tgargs", "tgqual", "tgoldtable", "tgnewtable"],
        ["pg_policy"] = ["oid", "polname", "polrelid", "polcmd", "polpermissive", "polroles", "polqual", "polwithcheck"],
        ["pg_policies"] = ["schemaname", "tablename", "policyname", "permissive", "roles", "cmd", "qual", "with_check"],
        ["pg_statistic_ext"] = ["oid", "stxrelid", "stxname", "stxnamespace", "stxowner", "stxkeys", "stxstattarget", "stxkind", "stxexprs"],
        ["pg_publication"] = ["oid", "pubname", "pubowner", "puballtables", "pubinsert", "pubupdate", "pubdelete", "pubtruncate", "pubviaroot", "pubgencols"],
        ["pg_publication_rel"] = ["oid", "prpubid", "prrelid", "prqual", "prattrs"],
        ["pg_publication_namespace"] = ["oid", "pnpubid", "pnnspid"],
        ["pg_publication_tables"] = ["pubname", "schemaname", "tablename", "attnames", "rowfilter"],
        ["pg_rewrite"] = ["oid", "rulename", "ev_class", "ev_type", "ev_enabled", "is_instead", "ev_qual", "ev_action"],
        ["pg_sequence"] = ["seqrelid", "seqtypid", "seqstart", "seqincrement", "seqmax", "seqmin", "seqcache", "seqcycle"],
        ["pg_sequences"] = ["schemaname", "sequencename", "sequenceowner", "data_type", "start_value", "min_value", "max_value", "increment_by", "cycle", "cache_size", "last_value"],
        ["pg_extension"] = ["oid", "extname", "extowner", "extnamespace", "extrelocatable", "extversion", "extconfig", "extcondition"],
        ["pg_enum"] = ["oid", "enumtypid", "enumsortorder", "enumlabel"],
        ["pg_range"] = ["rngtypid", "rngsubtype", "rngmultitypid", "rngcollation", "rngsubopc", "rngcanonical", "rngsubdiff"],
        ["pg_collation"] = ["oid", "collname", "collnamespace", "collowner", "collprovider", "collisdeterministic", "collencoding", "collcollate", "collctype", "colllocale", "collicurules", "collversion"],
        ["pg_depend"] = ["classid", "objid", "objsubid", "refclassid", "refobjid", "refobjsubid", "deptype"],
        ["pg_matviews"] = ["schemaname", "matviewname", "matviewowner", "tablespace", "hasindexes", "ispopulated", "definition"],
        ["pg_foreign_table"] = ["ftrelid", "ftserver", "ftoptions"],
        ["pg_partitioned_table"] = ["partrelid", "partstrat", "partnatts", "partdefid", "partattrs", "partclass", "partcollation", "partexprs"],
        ["pg_event_trigger"] = ["oid", "evtname", "evtevent", "evtowner", "evtfoid", "evtenabled", "evttags"],
        ["pg_opclass"] = ["oid", "opcmethod", "opcname", "opcnamespace", "opcowner", "opcfamily", "opcintype", "opcdefault", "opckeytype"],
        ["pg_operator"] = ["oid", "oprname", "oprnamespace", "oprowner", "oprkind", "oprcanmerge", "oprcanhash", "oprleft", "oprright", "oprresult", "oprcom", "oprnegate", "oprcode", "oprrest", "oprjoin"],
        ["pg_cast"] = ["oid", "castsource", "casttarget", "castfunc", "castcontext", "castmethod"],
        ["pg_language"] = ["oid", "lanname", "lanowner", "lanispl", "lanpltrusted", "lanplcallfoid", "laninline", "lanvalidator", "lanacl"],
        ["pg_auth_members"] = ["oid", "roleid", "member", "grantor", "admin_option", "inherit_option", "set_option"],
        ["pg_db_role_setting"] = ["setdatabase", "setrole", "setconfig"],
        ["pg_default_acl"] = ["oid", "defaclrole", "defaclnamespace", "defaclobjtype", "defaclacl"],
        ["pg_init_privs"] = ["objoid", "classoid", "objsubid", "privtype", "initprivs"],
        ["pg_seclabel"] = ["objoid", "classoid", "objsubid", "provider", "label"],
        ["pg_stat_activity"] = ["datid", "datname", "pid", "leader_pid", "usesysid", "usename", "application_name", "client_addr", "client_hostname", "client_port", "backend_start", "xact_start", "query_start", "state_change", "wait_event_type", "wait_event", "state", "backend_xid", "backend_xmin", "query_id", "query", "backend_type"],
        ["pg_locks"] = ["locktype", "database", "relation", "page", "tuple", "virtualxid", "transactionid", "classid", "objid", "objsubid", "virtualtransaction", "pid", "mode", "granted", "fastpath", "waitstart"],
        ["pg_prepared_xacts"] = ["transaction", "gid", "prepared", "owner", "database"],
        ["sequences"] = ["sequence_catalog", "sequence_schema", "sequence_name", "data_type", "numeric_precision", "numeric_precision_radix", "numeric_scale", "start_value", "minimum_value", "maximum_value", "increment", "cycle_option"],
        ["routines"] = ["specific_catalog", "specific_schema", "specific_name", "routine_catalog", "routine_schema", "routine_name", "routine_type", "data_type", "routine_definition", "external_language"],
        ["triggers"] = ["trigger_catalog", "trigger_schema", "trigger_name", "event_manipulation", "event_object_catalog", "event_object_schema", "event_object_table", "action_order", "action_condition", "action_statement", "action_orientation", "action_timing"],
        ["check_constraints"] = ["constraint_catalog", "constraint_schema", "constraint_name", "check_clause"],
        ["parameters"] = ["specific_catalog", "specific_schema", "specific_name", "ordinal_position", "parameter_mode", "parameter_name", "data_type"],
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenSet<string> InformationSchemaEmpty = ["sequences", "routines", "triggers", "check_constraints", "parameters"];

    public static string PgClassRelation => PgClass;

    public static string PgIndexRelation => PgIndex;

    public static string PgConstraintRelation => PgConstraint;

    public static bool IsAlwaysEmpty(string schema, string name) =>
        (schema == "pg_catalog" && name.StartsWith("pg_", StringComparison.Ordinal) && EmptyRelations.ContainsKey(name))
        || (schema == "information_schema" && InformationSchemaEmpty.Contains(name));

    public static bool TryGetRelation(string schema, string name, PgRewriteContext context, out string sql)
    {
        sql = string.Empty;
        var template = schema switch
        {
            "pg_catalog" => PgCatalogRelation(name, context),
            "information_schema" => InformationSchemaRelation(name),
            _ => null,
        };
        if (template is null)
        {
            return false;
        }

        sql = template
            .Replace(DatabaseToken, PgRewriteContext.Literal(context.DatabaseName), StringComparison.Ordinal)
            .Replace(UserToken, PgRewriteContext.Literal(context.UserName), StringComparison.Ordinal);
        return true;
    }

    public static string FormatTypeCase(string operand) =>
        $"(CASE {operand} {string.Concat(PgOid.Types.Select(static type => $"WHEN {type.Oid.ToString(CultureInfo.InvariantCulture)} THEN {PgRewriteContext.Literal(type.FormatName)} "))}ELSE 'text' END)";

    private static string? PgCatalogRelation(string name, PgRewriteContext context) => name switch
    {
        "pg_class" => PgClass,
        "pg_attribute" => PgAttribute,
        "pg_attrdef" => PgAttrdef,
        "pg_index" => PgIndex,
        "pg_constraint" => PgConstraint,
        "pg_type" => PgType,
        "pg_namespace" => "SELECT CAST(v.column1 AS INTEGER) AS oid, v.column2 AS nspname, CAST(10 AS INTEGER) AS nspowner, NULL AS nspacl FROM (VALUES (11, 'pg_catalog'), (2200, 'public'), (13000, 'information_schema')) v",
        "pg_am" => "SELECT CAST(v.column1 AS INTEGER) AS oid, v.column2 AS amname, CAST(0 AS INTEGER) AS amhandler, v.column3 AS amtype FROM (VALUES (2, 'heap', 't'), (403, 'btree', 'i')) v",
        "pg_tablespace" => "SELECT CAST(v.column1 AS INTEGER) AS oid, v.column2 AS spcname, CAST(10 AS INTEGER) AS spcowner, NULL AS spcacl, NULL AS spcoptions FROM (VALUES (1663, 'pg_default'), (1664, 'pg_global')) v",
        "pg_database" => $"SELECT CAST(16383 AS INTEGER) AS oid, {DatabaseToken} AS datname, CAST(10 AS INTEGER) AS datdba, CAST(6 AS INTEGER) AS encoding, 'c' AS datlocprovider, CAST(0 AS INTEGER) AS datistemplate, CAST(1 AS INTEGER) AS datallowconn, CAST(0 AS INTEGER) AS dathasloginevt, CAST(-1 AS INTEGER) AS datconnlimit, CAST(0 AS INTEGER) AS datfrozenxid, CAST(0 AS INTEGER) AS datminmxid, CAST(1663 AS INTEGER) AS dattablespace, 'C' AS datcollate, 'C' AS datctype, NULL AS datlocale, NULL AS daticulocale, NULL AS daticurules, NULL AS datcollversion, NULL AS datacl",
        "pg_roles" or "pg_authid" => $"SELECT CAST(10 AS INTEGER) AS oid, {UserToken} AS rolname, CAST(0 AS INTEGER) AS rolsuper, CAST(1 AS INTEGER) AS rolinherit, CAST(0 AS INTEGER) AS rolcreaterole, CAST(0 AS INTEGER) AS rolcreatedb, CAST(1 AS INTEGER) AS rolcanlogin, CAST(0 AS INTEGER) AS rolreplication, CAST(-1 AS INTEGER) AS rolconnlimit, '********' AS rolpassword, NULL AS rolvaliduntil, CAST(0 AS INTEGER) AS rolbypassrls, NULL AS rolconfig",
        "pg_user" => $"SELECT {UserToken} AS usename, CAST(10 AS INTEGER) AS usesysid, CAST(0 AS INTEGER) AS usecreatedb, CAST(0 AS INTEGER) AS usesuper, CAST(0 AS INTEGER) AS userepl, CAST(0 AS INTEGER) AS usebypassrls, '********' AS passwd, NULL AS valuntil, NULL AS useconfig",
        "pg_tables" => $"SELECT 'public' AS schemaname, o.name AS tablename, {UserToken} AS tableowner, NULL AS tablespace, CAST(EXISTS (SELECT 1 FROM pragma_index_list(o.name)) AS INTEGER) AS hasindexes, CAST(0 AS INTEGER) AS hasrules, CAST(EXISTS (SELECT 1 FROM sqlite_schema t WHERE t.type = 'trigger' AND t.tbl_name = o.name) AS INTEGER) AS hastriggers, CAST(0 AS INTEGER) AS rowsecurity FROM sqlite_schema o WHERE o.type = 'table' AND {UserObject("o")}",
        "pg_views" => $"SELECT 'public' AS schemaname, o.name AS viewname, {UserToken} AS viewowner, o.sql AS definition FROM sqlite_schema o WHERE o.type = 'view' AND {UserObject("o")}",
        "pg_indexes" => $"SELECT 'public' AS schemaname, x._tb_tablename AS tablename, x._tb_indexname AS indexname, NULL AS tablespace, x._tb_indexdef AS indexdef, x.indexrelid AS _tb_oid FROM ({PgIndex}) x",
        "pg_settings" => Settings(context),
        _ when EmptyRelations.TryGetValue(name, out var columns) && name.StartsWith("pg_", StringComparison.Ordinal) => Empty(columns),
        _ => null,
    };

    private static string? InformationSchemaRelation(string name) => name switch
    {
        "tables" => $"SELECT {DatabaseToken} AS table_catalog, 'public' AS table_schema, o.name AS table_name, CASE o.type WHEN 'view' THEN 'VIEW' ELSE 'BASE TABLE' END AS table_type, NULL AS self_referencing_column_name, NULL AS reference_generation, NULL AS user_defined_type_catalog, NULL AS user_defined_type_schema, NULL AS user_defined_type_name, CASE o.type WHEN 'view' THEN 'NO' ELSE 'YES' END AS is_insertable_into, 'NO' AS is_typed, NULL AS commit_action FROM sqlite_schema o WHERE o.type IN ('table', 'view') AND {UserObject("o")}",
        "columns" => $"""
            SELECT {DatabaseToken} AS table_catalog, 'public' AS table_schema, c._tb_relname AS table_name, c._tb_attname AS column_name, CAST(c._tb_cid + 1 AS INTEGER) AS ordinal_position,
                {DefaultExpression("c._tb_default")} AS column_default, CASE WHEN c._tb_notnull OR c._tb_pk > 0 THEN 'NO' ELSE 'YES' END AS is_nullable, {FormatBase} AS data_type,
                CAST(CASE WHEN c._tb_typid IN (1042, 1043) AND instr(c._tb_decl, '(') > 0 THEN CAST(substr(c._tb_decl, instr(c._tb_decl, '(') + 1) AS INTEGER) END AS INTEGER) AS character_maximum_length,
                NULL AS character_octet_length,
                CAST(CASE c._tb_typid WHEN 21 THEN 16 WHEN 23 THEN 32 WHEN 20 THEN 64 WHEN 700 THEN 24 WHEN 701 THEN 53 END AS INTEGER) AS numeric_precision,
                CAST(CASE WHEN c._tb_typid IN (20, 21, 23, 700, 701, 1700) THEN 2 END AS INTEGER) AS numeric_precision_radix,
                CAST(CASE WHEN c._tb_typid IN (20, 21, 23) THEN 0 END AS INTEGER) AS numeric_scale,
                CAST(CASE WHEN c._tb_typid IN (1082, 1083, 1114, 1184) THEN 6 END AS INTEGER) AS datetime_precision, NULL AS interval_type, NULL AS interval_precision,
                NULL AS character_set_catalog, NULL AS character_set_schema, NULL AS character_set_name, NULL AS collation_catalog, NULL AS collation_schema, NULL AS collation_name,
                NULL AS domain_catalog, NULL AS domain_schema, NULL AS domain_name, {DatabaseToken} AS udt_catalog, 'pg_catalog' AS udt_schema, {TypeName} AS udt_name,
                NULL AS scope_catalog, NULL AS scope_schema, NULL AS scope_name, NULL AS maximum_cardinality, CAST(c._tb_cid + 1 AS INTEGER) AS dtd_identifier,
                'NO' AS is_self_referencing, CASE WHEN {Identity} THEN 'YES' ELSE 'NO' END AS is_identity, CASE WHEN {Identity} THEN 'BY DEFAULT' END AS identity_generation,
                NULL AS identity_start, NULL AS identity_increment, NULL AS identity_maximum, NULL AS identity_minimum, CASE WHEN {Identity} THEN 'NO' END AS identity_cycle,
                'NEVER' AS is_generated, NULL AS generation_expression, CASE c._tb_reltype WHEN 'view' THEN 'NO' ELSE 'YES' END AS is_updatable
            FROM ({Columns}) c
            """,
        "schemata" => $"SELECT {DatabaseToken} AS catalog_name, v.column1 AS schema_name, {UserToken} AS schema_owner, NULL AS default_character_set_catalog, NULL AS default_character_set_schema, NULL AS default_character_set_name, NULL AS sql_path FROM (VALUES ('public'), ('information_schema'), ('pg_catalog')) v",
        "table_constraints" => $"SELECT {DatabaseToken} AS constraint_catalog, 'public' AS constraint_schema, k.conname AS constraint_name, {DatabaseToken} AS table_catalog, 'public' AS table_schema, k._tb_tablename AS table_name, CASE k.contype WHEN 'p' THEN 'PRIMARY KEY' WHEN 'u' THEN 'UNIQUE' WHEN 'f' THEN 'FOREIGN KEY' ELSE 'CHECK' END AS constraint_type, 'NO' AS is_deferrable, 'NO' AS initially_deferred, 'YES' AS enforced, CASE WHEN k.contype = 'u' THEN 'YES' END AS nulls_distinct FROM ({PgConstraint}) k",
        "key_column_usage" => $"""
            SELECT {DatabaseToken} AS constraint_catalog, 'public' AS constraint_schema, k.constraint_name AS constraint_name, {DatabaseToken} AS table_catalog, 'public' AS table_schema,
                k.table_name AS table_name, k.column_name AS column_name, CAST(k.ordinal_position AS INTEGER) AS ordinal_position, CAST(k.unique_position AS INTEGER) AS position_in_unique_constraint
            FROM (
                SELECT t.name || '_pkey' AS constraint_name, t.name AS table_name, p.name AS column_name, p.pk AS ordinal_position, NULL AS unique_position
                FROM sqlite_schema t JOIN pragma_table_info(t.name) p WHERE t.type = 'table' AND {UserObject("t")} AND p.pk > 0
                UNION ALL
                SELECT {IndexName("il.name", "t.name", "il.origin")}, t.name, ii.name, ii.seqno + 1, NULL
                FROM sqlite_schema t JOIN pragma_index_list(t.name) il JOIN pragma_index_info(il.name) ii WHERE t.type = 'table' AND {UserObject("t")} AND il.origin = 'u'
                UNION ALL
                SELECT {ForeignKeyName}, t.name, f."from", f.seq + 1, f.seq + 1
                FROM sqlite_schema t JOIN pragma_foreign_key_list(t.name) f WHERE t.type = 'table' AND {UserObject("t")}
            ) k
            """,
        "referential_constraints" => $"SELECT {DatabaseToken} AS constraint_catalog, 'public' AS constraint_schema, {ForeignKeyName} AS constraint_name, {DatabaseToken} AS unique_constraint_catalog, 'public' AS unique_constraint_schema, f.\"table\" || '_pkey' AS unique_constraint_name, 'NONE' AS match_option, f.on_update AS update_rule, f.on_delete AS delete_rule FROM sqlite_schema t JOIN pragma_foreign_key_list(t.name) f WHERE t.type = 'table' AND {UserObject("t")} AND f.seq = 0",
        "constraint_column_usage" => $"""
            SELECT {DatabaseToken} AS table_catalog, 'public' AS table_schema, k.table_name AS table_name, k.column_name AS column_name, {DatabaseToken} AS constraint_catalog,
                'public' AS constraint_schema, k.constraint_name AS constraint_name
            FROM (
                SELECT t.name || '_pkey' AS constraint_name, t.name AS table_name, p.name AS column_name
                FROM sqlite_schema t JOIN pragma_table_info(t.name) p WHERE t.type = 'table' AND {UserObject("t")} AND p.pk > 0
                UNION ALL
                SELECT {IndexName("il.name", "t.name", "il.origin")}, t.name, ii.name
                FROM sqlite_schema t JOIN pragma_index_list(t.name) il JOIN pragma_index_info(il.name) ii WHERE t.type = 'table' AND {UserObject("t")} AND il.origin = 'u'
                UNION ALL
                SELECT {ForeignKeyName}, f."table", f."to"
                FROM sqlite_schema t JOIN pragma_foreign_key_list(t.name) f WHERE t.type = 'table' AND {UserObject("t")}
            ) k
            """,
        "views" => $"SELECT {DatabaseToken} AS table_catalog, 'public' AS table_schema, o.name AS table_name, o.sql AS view_definition, 'NONE' AS check_option, 'NO' AS is_updatable, 'NO' AS is_insertable_into, 'NO' AS is_trigger_updatable, 'NO' AS is_trigger_deletable, 'NO' AS is_trigger_insertable_into FROM sqlite_schema o WHERE o.type = 'view' AND {UserObject("o")}",
        _ when InformationSchemaEmpty.Contains(name) => Empty(EmptyRelations[name]),
        _ => null,
    };

    private static string UserObject(string alias) =>
        $"({alias}.name NOT LIKE 'sqlite\\_%' ESCAPE '\\' OR {alias}.name LIKE 'sqlite\\_autoindex\\_%' ESCAPE '\\') AND {alias}.name NOT LIKE '\\_tb\\_%' ESCAPE '\\' AND {alias}.tbl_name NOT LIKE '\\_tb\\_%' ESCAPE '\\'";

    private static string RowidPrimaryKey(string alias) =>
        $"{alias}.type = 'table' AND {UserObject(alias)} AND NOT EXISTS (SELECT 1 FROM pragma_index_list({alias}.name) WHERE origin = 'pk') AND EXISTS (SELECT 1 FROM pragma_table_info({alias}.name) WHERE pk > 0)";

    private static string IndexName(string index, string table, string origin) =>
        $"(CASE WHEN {index} LIKE 'sqlite\\_autoindex\\_%' ESCAPE '\\' THEN (CASE {origin} WHEN 'pk' THEN {table} || '_pkey' ELSE {table} || '_' || (SELECT group_concat(ii.name, '_') FROM pragma_index_info({index}) ii) || '_key' END) ELSE {index} END)";

    private static string DefaultExpression(string column) =>
        $"(CASE {column} WHEN {PgRewriteContext.Literal(PostgresSqlRewriter.NowExpression)} THEN 'now()' WHEN {PgRewriteContext.Literal(PostgresSqlRewriter.UuidExpression)} THEN 'gen_random_uuid()' ELSE {column} END)";

    private static string Action(string column) =>
        $"CASE {column} WHEN 'CASCADE' THEN 'c' WHEN 'SET NULL' THEN 'n' WHEN 'SET DEFAULT' THEN 'd' WHEN 'RESTRICT' THEN 'r' ELSE 'a' END";

    private static string Empty(IReadOnlyList<string> columns) =>
        "SELECT " + string.Join(", ", columns.Select(static column => "NULL AS " + column)) + " WHERE 0";

    private static string Settings(PgRewriteContext context)
    {
        var rows = string.Join(", ", context.Settings.Select(static pair => $"({PgRewriteContext.Literal(pair.Key)}, {PgRewriteContext.Literal(pair.Value)})"));
        return $"SELECT v.column1 AS name, v.column2 AS setting, NULL AS unit, 'Ungrouped' AS category, '' AS short_desc, NULL AS extra_desc, 'user' AS context, 'string' AS vartype, 'session' AS source, NULL AS min_val, NULL AS max_val, NULL AS enumvals, v.column2 AS boot_val, v.column2 AS reset_val, NULL AS sourcefile, NULL AS sourceline, CAST(0 AS INTEGER) AS pending_restart FROM (VALUES {rows}) v";
    }

    private static string BuildTypeOid(string operand)
    {
        var normalized = $"upper(trim(CASE WHEN instr({operand}, '(') > 0 THEN substr({operand}, 1, instr({operand}, '(') - 1) ELSE {operand} END))";
        var builder = new StringBuilder($"CASE {normalized} ");
        foreach (var (name, oid) in PgOid.DeclaredNames)
        {
            builder.Append(CultureInfo.InvariantCulture, $"WHEN {PgRewriteContext.Literal(name)} THEN {oid} ");
        }

        return builder.Append("ELSE 25 END").ToString();
    }

    private static string BuildCase(string operand, Func<PgType, string> value, string fallback, bool quote = true)
    {
        var builder = new StringBuilder($"CASE {operand} ");
        foreach (var type in PgOid.Types)
        {
            var rendered = value(type);
            builder.Append(CultureInfo.InvariantCulture, $"WHEN {type.Oid} THEN {(quote ? PgRewriteContext.Literal(rendered) : rendered)} ");
        }

        return builder.Append(CultureInfo.InvariantCulture, $"ELSE {(quote ? PgRewriteContext.Literal(fallback) : fallback)} END").ToString();
    }

    private static string BuildPgType()
    {
        var rows = new List<string>();
        foreach (var type in PgOid.Types)
        {
            var collation = type.Category == 'S' ? 100 : 0;
            rows.Add(string.Create(CultureInfo.InvariantCulture, $"({type.Oid}, '{type.Name}', {type.Length}, '{(type.Oid == PgOid.Unknown ? 'p' : 'b')}', '{type.Category}', 0, {type.ArrayOid}, '{type.Name}recv', {collation})"));
            if (type.ArrayOid != 0)
            {
                rows.Add(string.Create(CultureInfo.InvariantCulture, $"({type.ArrayOid}, '_{type.Name}', -1, 'b', 'A', {type.Oid}, 0, 'array_recv', {collation})"));
            }
        }

        return $"""
            SELECT CAST(v.column1 AS INTEGER) AS oid, v.column2 AS typname, CAST(11 AS INTEGER) AS typnamespace, CAST(10 AS INTEGER) AS typowner, CAST(v.column3 AS INTEGER) AS typlen,
                CAST(v.column3 IN (1, 2, 4, 8) AS INTEGER) AS typbyval, v.column4 AS typtype, v.column5 AS typcategory, CAST(0 AS INTEGER) AS typispreferred,
                CAST(1 AS INTEGER) AS typisdefined, ',' AS typdelim, CAST(0 AS INTEGER) AS typrelid, CAST(0 AS INTEGER) AS typsubscript, CAST(v.column6 AS INTEGER) AS typelem,
                CAST(v.column7 AS INTEGER) AS typarray, v.column2 || 'in' AS typinput, v.column2 || 'out' AS typoutput, v.column8 AS typreceive, v.column2 || 'send' AS typsend,
                CAST(0 AS INTEGER) AS typmodin, CAST(0 AS INTEGER) AS typmodout, CAST(0 AS INTEGER) AS typanalyze, 'i' AS typalign, 'x' AS typstorage,
                CAST(0 AS INTEGER) AS typnotnull, CAST(0 AS INTEGER) AS typbasetype, CAST(-1 AS INTEGER) AS typtypmod, CAST(0 AS INTEGER) AS typndims,
                CAST(v.column9 AS INTEGER) AS typcollation, NULL AS typdefaultbin, NULL AS typdefault, NULL AS typacl
            FROM (VALUES {string.Join(", ", rows)}) v
            """;
    }
}
