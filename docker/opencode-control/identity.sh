# Sourced only after acquiring the state-volume lock. Values enter JSON only
# after strict validation; do not take the service directory from an environment.
control_directory=/var/lib/opencode/workspaces/control
identity_file=/var/lib/opencode/state/agentcontrol-instance-id
manifest_file="$control_directory/.agentcontrol-service.json"

valid_uuid() {
    printf '%s\n' "$1" | grep -Eq '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
}

if [ -e "$identity_file" ] || [ -L "$identity_file" ]; then
    if [ ! -f "$identity_file" ] || [ -L "$identity_file" ] || [ "$(wc -c < "$identity_file")" -ne 37 ]; then
        echo 'OpenCode control instance identity is invalid; refusing to replace it.' >&2
        exit 1
    fi
    instance_id=$(cat "$identity_file")
    if ! valid_uuid "$instance_id"; then
        echo 'OpenCode control instance identity is invalid; refusing to replace it.' >&2
        exit 1
    fi
else
    if [ -e "$manifest_file" ] || [ -L "$manifest_file" ]; then
        echo 'OpenCode control instance identity is missing for existing state; refusing to replace it.' >&2
        exit 1
    fi
    instance_id=$(cat /proc/sys/kernel/random/uuid)
    valid_uuid "$instance_id"
    identity_temp=$(mktemp /var/lib/opencode/state/.agentcontrol-instance-id.XXXXXX)
    printf '%s\n' "$instance_id" > "$identity_temp"
    mv "$identity_temp" "$identity_file"
fi

incarnation_id=$(cat /proc/sys/kernel/random/uuid)
valid_uuid "$incarnation_id"
started_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
manifest_temp=$(mktemp "$control_directory/.agentcontrol-service.json.XXXXXX")
printf '{"schemaVersion":1,"instanceId":"%s","incarnationId":"%s","startedAt":"%s","directory":"%s"}\n' \
    "$instance_id" "$incarnation_id" "$started_at" "$control_directory" > "$manifest_temp"
mv "$manifest_temp" "$manifest_file"
unset instance_id incarnation_id started_at identity_temp manifest_temp
