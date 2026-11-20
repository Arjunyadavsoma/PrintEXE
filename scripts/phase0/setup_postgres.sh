#!/usr/bin/env bash
# Phase 0 setup: portable PostgreSQL 16 (no docker, no root in sandbox).
# Zonky embedded-postgres binaries from Maven Central, extracted to tools/pg16,
# data dir tools/pgdata, listening on 127.0.0.1:5433 (sandbox-local only).
set -e
cd /tmp/pgdl
curl -sSL -o pg.jar https://repo1.maven.org/maven2/io/zonky/test/postgres/embedded-postgres-binaries-linux-amd64/16.2.0/embedded-postgres-binaries-linux-amd64-16.2.0.jar
rm -rf extracted && mkdir extracted
unzip -o -q pg.jar -d extracted
mkdir -p /home/z/my-project/tools/pg16
tar -xJf extracted/postgres-linux-x86_64.txz -C /home/z/my-project/tools/pg16
export PGHOME=/home/z/my-project/tools/pg16
export PGDATA=/home/z/my-project/tools/pgdata
rm -rf "$PGDATA"
mkdir -p "$PGDATA"
echo printshop > /tmp/pgdl/pwfile
"$PGHOME/bin/initdb" -D "$PGDATA" -U printshop --pwfile=/tmp/pgdl/pwfile -E UTF8 --auth-local=trust --auth-host=trust
"$PGHOME/bin/pg_ctl" -D "$PGDATA" -o "-p 5433 -c listen_addresses=127.0.0.1" -l /home/z/my-project/tools/pgdata.log start
sleep 2
"$PGHOME/bin/psql" -h 127.0.0.1 -p 5433 -U printshop -d postgres -c "CREATE DATABASE printshop;" || true
"$PGHOME/bin/psql" -h 127.0.0.1 -p 5433 -U printshop -d postgres -c "SELECT version();"
echo "POSTGRES OK on 127.0.0.1:5433 (user printshop, db printshop)"
