#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

IMAGE="${IMAGE:-cron-backup:integration-test}"
PROJECT="${PROJECT:-cronbackup-integration}"
NETWORK_NAME="${NETWORK_NAME:-cronbackup-integration-net}"
DAEMON_NAME="${DAEMON_NAME:-cronbackup-integration-daemon}"
KEEP_TEST_ROOT="${KEEP_TEST_ROOT:-0}"
TEST_ROOT="${TEST_ROOT:-$(mktemp -d)}"

if [[ "$TEST_ROOT" != /* ]]; then
    TEST_ROOT="$(cd "$TEST_ROOT" && pwd)"
fi

export TEST_ROOT PROJECT NETWORK_NAME
export MINIO_IMAGE="${MINIO_IMAGE:-minio/minio:latest}"
export SFTP_IMAGE="${SFTP_IMAGE:-atmoz/sftp:alpine}"

compose() {
    docker compose -p "$PROJECT" -f "$SCRIPT_DIR/docker-compose.yml" "$@"
}

cleanup() {
    docker rm -f "$DAEMON_NAME" >/dev/null 2>&1 || true
    compose down -v >/dev/null 2>&1 || true
    if [[ "$KEEP_TEST_ROOT" != "1" ]]; then
        # MinIO/SFTP 容器会以各自的 UID 写入 bind mount，宿主 docker 用户
        # 不一定能直接删；最后用镜像的 root 身份清一次。
        docker run --rm -u 0 -v "$TEST_ROOT:/cleanup" --entrypoint sh "$IMAGE" \
            -c 'rm -rf /cleanup/*' >/dev/null 2>&1 || true
        rm -rf "$TEST_ROOT" >/dev/null 2>&1 || true
    else
        echo "test root kept at: $TEST_ROOT"
    fi
}
trap cleanup EXIT

assert_eq() {
    local expected="$1"
    local actual="$2"
    local message="$3"
    if [[ "$actual" != "$expected" ]]; then
        echo "FAIL: $message (expected '$expected', got '$actual')" >&2
        exit 1
    fi
}

count_objects() {
    local remote="$1"
    rclone_ls lsf --files-only "$remote" | wc -l | tr -d '[:space:]'
}

run_backup() {
    docker run --rm --network "$NETWORK_NAME" \
        -v "$SCRIPT_DIR/config.yaml:/config/config.yaml:ro" \
        -v "$SCRIPT_DIR/daemon.yaml:/config/daemon.yaml:ro" \
        -v "$TEST_ROOT/source:/data:ro" \
        -v "$TEST_ROOT/restore:/restore" \
        -e S3_KEY=testuser \
        -e S3_SECRET=testpassword \
        -e SFTP_PASS=testpassword \
        "$IMAGE" "$@"
}

wait_for() {
    local description="$1"
    shift
    local attempt
    for attempt in $(seq 1 60); do
        if "$@" >/dev/null 2>&1; then
            return 0
        fi
        sleep 1
    done
    echo "timed out waiting for $description" >&2
    return 1
}

echo "==> preparing test data at $TEST_ROOT"
mkdir -p \
    "$TEST_ROOT/source/nested" \
    "$TEST_ROOT/minio-data" \
    "$TEST_ROOT/sftp-data" \
    "$TEST_ROOT/restore/s3" \
    "$TEST_ROOT/restore/sftp"
# 容器内应用用户 UID 1000 要往 /restore 写；CI runner 的 UID 不一定是 1000。
chmod 777 "$TEST_ROOT/restore" "$TEST_ROOT/restore/s3" "$TEST_ROOT/restore/sftp"
printf 'hello from integration test' > "$TEST_ROOT/source/hello.txt"
dd if=/dev/urandom of="$TEST_ROOT/source/nested/payload.bin" bs=1024 count=1024 status=none

echo "==> resolving images"
if ! docker image inspect "$MINIO_IMAGE" >/dev/null 2>&1; then
    if ! docker pull "$MINIO_IMAGE" >/dev/null 2>&1; then
        echo "official MinIO image unavailable, falling back to elestio/minio"
        export MINIO_IMAGE="elestio/minio:latest"
        docker pull "$MINIO_IMAGE" >/dev/null
    fi
fi
if ! docker image inspect "$SFTP_IMAGE" >/dev/null 2>&1; then
    docker pull "$SFTP_IMAGE" >/dev/null
fi

echo "==> building $IMAGE"
docker build --build-arg RCLONE_VERSION=1.76.0 -t "$IMAGE" "$REPO_ROOT"

echo "==> preparing rclone test config"
SFTP_PASS_OBSCURED="$(
    docker run --rm --entrypoint rclone "$IMAGE" obscure testpassword | tr -d '\r\n'
)"
cat > "$TEST_ROOT/rclone.conf" <<EOF
[minio]
type = s3
provider = Minio
access_key_id = testuser
secret_access_key = testpassword
endpoint = http://minio:9000
region = us-east-1
no_check_bucket = false

[sftp]
type = sftp
host = sftp
user = testuser
pass = $SFTP_PASS_OBSCURED
known_hosts_file = none
EOF
# rclone 偶尔会尝试回写 conf；单个文件 bind mount 是挂载点，不允许 rename。
# 把 conf 放进一个可写目录再挂载目录，容器内就能正常回写。
mkdir -p "$TEST_ROOT/rclone"
mv "$TEST_ROOT/rclone.conf" "$TEST_ROOT/rclone/rclone.conf"
chmod 777 "$TEST_ROOT/rclone"
chmod 666 "$TEST_ROOT/rclone/rclone.conf"

rclone_ls() {
    docker run --rm --network "$NETWORK_NAME" \
        -v "$TEST_ROOT/rclone:/rclone" \
        --entrypoint rclone \
        "$IMAGE" --config /rclone/rclone.conf "$@"
}

echo "==> starting MinIO and SFTP"
compose up -d
wait_for "MinIO" rclone_ls lsd minio:
wait_for "SFTP" rclone_ls lsd sftp:
compose exec -T sftp chown -R 1001:100 /home/testuser/backup

echo "==> running backup three times"
for run in 1 2 3; do
    echo "--- run $run"
    run_backup run --once --config /config/config.yaml --log-level error
    sleep 2
done

echo "==> asserting retention"
assert_eq "2" "$(count_objects minio:backups/integration/daily)" "S3 daily count"
assert_eq "1" "$(count_objects minio:backups/integration/weekly)" "S3 weekly count"
assert_eq "1" "$(count_objects minio:backups/integration/monthly)" "S3 monthly count"
assert_eq "2" "$(count_objects sftp:backup/integration/daily)" "SFTP daily count"
assert_eq "1" "$(count_objects sftp:backup/integration/weekly)" "SFTP weekly count"
assert_eq "1" "$(count_objects sftp:backup/integration/monthly)" "SFTP monthly count"

echo "==> asserting remote listing"
LIST_OUTPUT="$(run_backup list --remote --job integration --config /config/config.yaml --log-level error)"
grep -q "daily/" <<< "$LIST_OUTPUT"

echo "==> asserting restore from S3"
run_backup restore --job integration --destination s3 --latest \
    --to /restore/s3 --extract --config /config/config.yaml --log-level error
cmp "$TEST_ROOT/source/hello.txt" "$TEST_ROOT/restore/s3/hello.txt"
cmp "$TEST_ROOT/source/nested/payload.bin" "$TEST_ROOT/restore/s3/nested/payload.bin"

echo "==> asserting restore from SFTP"
run_backup restore --job integration --destination sftp --latest \
    --to /restore/sftp --extract --config /config/config.yaml --log-level error
cmp "$TEST_ROOT/source/hello.txt" "$TEST_ROOT/restore/sftp/hello.txt"
cmp "$TEST_ROOT/source/nested/payload.bin" "$TEST_ROOT/restore/sftp/nested/payload.bin"

echo "==> asserting prune"
# touch 会创建一个零字节对象，再把它改成很旧的时间，作为 prune 候选。
rclone_ls touch minio:backups/integration/daily/old-to-prune.tar.gz \
    --timestamp 2020-01-01T00:00:00Z
PRUNE_DRY_RUN="$(run_backup prune --job integration --config /config/config.yaml --log-level error)"
grep -q "old-to-prune.tar.gz" <<< "$PRUNE_DRY_RUN"
run_backup prune --job integration --config /config/config.yaml --apply --log-level error
if rclone_ls lsf --files-only minio:backups/integration/daily | grep -q "old-to-prune.tar.gz"; then
    echo "FAIL: prune did not delete old object" >&2
    exit 1
fi

echo "==> asserting daemon health and SIGTERM"
docker run -d --name "$DAEMON_NAME" --network "$NETWORK_NAME" \
    -v "$SCRIPT_DIR/daemon.yaml:/config/daemon.yaml:ro" \
    -v "$TEST_ROOT/source:/data:ro" \
    -e S3_KEY=testuser \
    -e S3_SECRET=testpassword \
    "$IMAGE" run --config /config/daemon.yaml --log-level error >/dev/null
sleep 6
docker exec "$DAEMON_NAME" /app/cron-backup health
docker stop "$DAEMON_NAME" >/dev/null
assert_eq "0" "$(docker inspect -f '{{.State.ExitCode}}' "$DAEMON_NAME")" "daemon exit code"

echo "integration tests passed"
