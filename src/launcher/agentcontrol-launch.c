/*
 * agentcontrol-launch - narrow privileged launcher for the AgentControl control
 * container.
 *
 * The container runs two unprivileged identities: the controller (UID 1001,
 * GID 1001) owns the private runtime state and the owner secret; the agent
 * (UID 1000, GID 1000) owns the OpenCode home, workspace, tmux server and every
 * model-facing process. The controller cannot change its own identity, so this
 * binary is the only privileged component: it is installed root-owned, group
 * `control`, mode 4750, so the agent identity cannot execute it at all.
 *
 * It exposes four fixed operations and nothing else:
 *
 *   acp <hostname> <port> <cwd>   exec OPENCODE_PATH "acp --hostname H --port P --cwd C"
 *   tmux <subcommand> [args...]   exec TMUX_PATH with an allow-listed subcommand
 *   pty <home> <session>          exec PYTHON_PATH "-u" BRIDGE_PATH <session>
 *   signal <pid> TERM|KILL        signal one verified agent child of the caller's parent
 *
 * The executable is always one of the four compiled-in paths; there is no
 * parameter for a program, a UID, a capability set or an environment block.
 *
 * The `tmux` operation is deliberately different from the other three, and the
 * difference is a real one:
 *
 *   The binary is fixed and the subcommand is allow-listed, but the remaining
 *   arguments are the caller's. `new-session`/`new-window` take a child command
 *   vector, so a caller that can invoke this operation CAN run an arbitrary
 *   program - including a shell - inside a tmux pane. That is the intended
 *   contract: OpenCode's TUI is exactly such a child. What the operation cannot
 *   do is run that program as anything other than the agent identity, and the
 *   agent identity is the one the controller already drives by design. It is a
 *   confinement boundary, not an execution allow-list.
 *
 *   Subcommands that would execute outside a pane or take over the server
 *   (`run-shell`, `kill-server`, `source-file`, `if-shell`, ...) are not in the
 *   list, so the operation stays a pane/session interface.
 *
 * Every exec operation irreversibly drops to the agent identity (supplementary
 * groups cleared, real/effective/saved UID and GID set, verified afterwards),
 * sets PR_SET_NO_NEW_PRIVS, leaves the child with empty permitted and effective
 * capability sets, rebuilds the environment from a closed name allow-list,
 * closes inherited descriptors above stderr, and starts a new process group. A
 * compromise of the controller can therefore execute code as the agent - which
 * it already does by design - but never as root, as the controller's own UID, or
 * as any other identity.
 *
 * The child's capability *bounding* set is not emptied here, and this file must
 * not claim otherwise. PR_CAPBSET_DROP requires CAP_SETPCAP, which the entrypoint
 * deliberately removes before the controller starts, so the attempt below is a
 * no-op (EPERM) in the shipped configuration. What actually bounds the child is
 * the set the entrypoint installed - SETUID | SETGID | KILL - which is why the
 * entrypoint, not this binary, is where CHOWN/DAC_OVERRIDE/FOWNER are removed.
 * StartupCapabilitiesAreRemovedFromTheControllerBoundingSet measures the child's
 * bounding set as 0xE0 rather than 0, which is the evidence for this wording.
 *
 * The `signal` operation is the only one that stays privileged, because a
 * UID 1001 parent cannot signal its UID 1000 children. It refuses anything that
 * is not a live process whose parent is the caller's parent (that is, a child of
 * the controller) and whose real, effective, saved and filesystem UIDs are all
 * the agent UID.
 */

#define _GNU_SOURCE

#include <ctype.h>
#include <errno.h>
#include <fcntl.h>
#include <grp.h>
#include <limits.h>
#include <signal.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/prctl.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/types.h>
#include <unistd.h>

#ifndef CONTROL_UID
#define CONTROL_UID 1001
#endif
#ifndef CONTROL_GID
#define CONTROL_GID 1001
#endif
#ifndef AGENT_UID
#define AGENT_UID 1000
#endif
#ifndef AGENT_GID
#define AGENT_GID 1000
#endif

#ifndef OPENCODE_PATH
#define OPENCODE_PATH "/usr/local/bin/opencode"
#endif
#ifndef TMUX_PATH
#define TMUX_PATH "/usr/bin/tmux"
#endif
#ifndef PYTHON_PATH
#define PYTHON_PATH "/usr/bin/python3"
#endif
#ifndef BRIDGE_PATH
#define BRIDGE_PATH "/app/Terminal/pty_bridge.py"
#endif

/* The agent data root. Every caller-supplied directory must live under it. */
#ifndef AGENT_DATA_PREFIX
#define AGENT_DATA_PREFIX "/data/"
#endif

#define FIXED_PATH "/usr/local/bin:/usr/local/sbin:/usr/bin:/usr/sbin:/bin:/sbin"

#define MAX_ARGUMENTS 256
#define MAX_ARGUMENT_BYTES 4096
#define MAX_ENVIRONMENT_BYTES (256 * 1024)

/*
 * Names copied from the caller's environment. Everything else - including every
 * loader, interpreter and runtime hook (LD_*, PYTHON*, NODE_OPTIONS, BASH_ENV,
 * DOTNET_*) - is dropped. The dropped-privilege child is no longer setuid, so
 * the kernel's own secure-execution filtering would not protect it; this list
 * is the actual boundary.
 */
static const char *const kEnvironmentAllowList[] = {
    "HOME",
    "LANG",
    "LC_ALL",
    "TERM",
    "COLORTERM",
    "TZ",
    "XDG_DATA_HOME",
    "XDG_CONFIG_HOME",
    "XDG_STATE_HOME",
    "XDG_CACHE_HOME",
    "OPENCODE_CONFIG_CONTENT",
    "OPENCODE_SERVER_USERNAME",
    "OPENCODE_SERVER_PASSWORD",
    "AGENTCONTROL_OWNER",
};

/*
 * tmux subcommands the controller actually issues. `new-session`/`new-window`
 * accept a child command vector, so this list bounds which tmux *interface* is
 * reachable, not which programs can run in a pane - see the file header. The
 * subcommands that execute outside a pane or reconfigure the server
 * (`run-shell`, `if-shell`, `source-file`, `kill-server`) are excluded.
 */
static const char *const kTmuxSubcommands[] = {
    "has-session",
    "list-sessions",
    "list-panes",
    "new-session",
    "new-window",
    "show-options",
    "set-option",
    "show-environment",
    "select-window",
    "select-pane",
    "kill-session",
};

static void fail(const char *message)
{
    fprintf(stderr, "agentcontrol-launch: %s\n", message);
    _exit(64);
}

static int string_in(const char *value, const char *const *table, size_t count)
{
    for (size_t index = 0; index < count; index++) {
        if (strcmp(value, table[index]) == 0) {
            return 1;
        }
    }
    return 0;
}

static int is_bounded(const char *value)
{
    return value != NULL && strnlen(value, MAX_ARGUMENT_BYTES + 1) <= MAX_ARGUMENT_BYTES;
}

static int is_digits(const char *value, size_t maximum_length)
{
    size_t length = strlen(value);
    if (length == 0 || length > maximum_length) {
        return 0;
    }
    for (size_t index = 0; index < length; index++) {
        if (!isdigit((unsigned char)value[index])) {
            return 0;
        }
    }
    return 1;
}

/*
 * Accepts an absolute, already-normalized path under the agent data root. No
 * relative segment, duplicate separator, trailing separator or shell/format
 * metacharacter is allowed, so the value cannot escape the prefix textually.
 *
 * This is only the textual half. A symlink *inside* /data could still resolve
 * out of the tree, so enter_agent_directory() re-checks the directory the
 * process actually landed in, after the chdir and after the privilege drop.
 */
static int is_agent_directory(const char *value)
{
    size_t length = strlen(value);
    if (length < strlen(AGENT_DATA_PREFIX) || length > PATH_MAX) {
        return 0;
    }
    if (strncmp(value, AGENT_DATA_PREFIX, strlen(AGENT_DATA_PREFIX)) != 0) {
        return 0;
    }
    if (value[length - 1] == '/') {
        return 0;
    }
    for (size_t index = 0; index < length; index++) {
        char c = value[index];
        int ok = isalnum((unsigned char)c) || c == '/' || c == '.' || c == '_' || c == '-';
        if (!ok) {
            return 0;
        }
        if (c == '/' && index + 1 < length && value[index + 1] == '/') {
            return 0;
        }
    }
    if (strstr(value, "/../") != NULL || strstr(value, "/./") != NULL) {
        return 0;
    }
    length = strlen(value);
    if (length >= 3 && strcmp(value + length - 3, "/..") == 0) {
        return 0;
    }
    return 1;
}

/* Loopback only: the OpenCode native HTTP server must never bind off-container. */
static int is_loopback_hostname(const char *value)
{
    if (strcmp(value, "localhost") == 0 || strcmp(value, "::1") == 0) {
        return 1;
    }
    if (strncmp(value, "127.", 4) != 0) {
        return 0;
    }
    size_t length = strlen(value);
    if (length > 15) {
        return 0;
    }
    for (size_t index = 4; index < length; index++) {
        if (!isdigit((unsigned char)value[index]) && value[index] != '.') {
            return 0;
        }
    }
    return 1;
}

static int is_port(const char *value)
{
    if (!is_digits(value, 5)) {
        return 0;
    }
    long port = strtol(value, NULL, 10);
    return port >= 1 && port <= 65535;
}

/* Mirrors TerminalProtocol.IsValidSessionName in the C# host. */
static int is_session_name(const char *value)
{
    size_t length = strlen(value);
    if (length == 0 || length > 64) {
        return 0;
    }
    for (size_t index = 0; index < length; index++) {
        char c = value[index];
        if (!isalnum((unsigned char)c) && c != '_' && c != '-') {
            return 0;
        }
    }
    return 1;
}

static char **build_environment(void)
{
    size_t allowed = sizeof(kEnvironmentAllowList) / sizeof(kEnvironmentAllowList[0]);
    char **block = calloc(allowed + 2, sizeof(char *));
    if (block == NULL) {
        fail("environment allocation failed");
    }

    size_t used = 0;
    size_t total = 0;
    block[used++] = (char *)"PATH=" FIXED_PATH;

    for (size_t index = 0; index < allowed; index++) {
        const char *name = kEnvironmentAllowList[index];
        const char *value = getenv(name);
        if (value == NULL) {
            continue;
        }

        size_t entry = strlen(name) + strlen(value) + 2;
        total += entry;
        if (total > MAX_ENVIRONMENT_BYTES) {
            fail("environment exceeds the supported size");
        }

        char *entry_text = malloc(entry);
        if (entry_text == NULL) {
            fail("environment allocation failed");
        }
        snprintf(entry_text, entry, "%s=%s", name, value);
        block[used++] = entry_text;
    }

    block[used] = NULL;
    return block;
}

/*
 * Closes everything above stderr before the identity change.
 *
 * Ordering matters and is deliberate: this runs while we are still root, so a
 * descriptor the controller leaked - an open private-state file, a socket, a
 * log - is closed before any agent-identity code can reach it. Doing it after
 * become_agent() would leave a window in which the descriptor still exists, and
 * an inherited descriptor bypasses file permissions entirely: the agent would
 * hold access to an object it could never have opened by path.
 */
static void close_inherited_descriptors(void)
{
    /* stdin/stdout/stderr are the controller's intended ACP/PTY pipes. */
#if defined(__NR_close_range) || defined(SYS_close_range)
    if (syscall(SYS_close_range, 3, ~0U, 0) == 0) {
        return;
    }
#endif
    long maximum = sysconf(_SC_OPEN_MAX);
    if (maximum < 0 || maximum > 65536) {
        maximum = 65536;
    }
    for (long descriptor = 3; descriptor < maximum; descriptor++) {
        (void)close((int)descriptor);
    }
}

/*
 * Best effort: PR_CAPBSET_DROP needs CAP_SETPCAP, which the container does not
 * grant, so EPERM is the normal outcome. It is not the protection that matters -
 * the setresuid() below clears the permitted and effective sets, and
 * no_new_privs stops the child reacquiring anything - but dropping the bounding
 * set as well costs nothing where it is permitted.
 */
static void drop_capability_bounding_set(void)
{
    for (int capability = 0; capability <= 63; capability++) {
        if (prctl(PR_CAPBSET_DROP, capability, 0, 0, 0) != 0 && errno != EINVAL && errno != EPERM) {
            fail("could not drop the capability bounding set");
        }
    }
}

static void become_agent(void)
{
    if (prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) != 0) {
        fail("could not set no_new_privs");
    }

    drop_capability_bounding_set();

    /* Groups first: after the UID change this would no longer be permitted. */
    if (setgroups(0, NULL) != 0) {
        fail("could not clear supplementary groups");
    }

    /* setres*id sets the real, effective and saved ids in one explicit step. */
    if (setresgid(AGENT_GID, AGENT_GID, AGENT_GID) != 0) {
        fail("could not assume the agent group");
    }
    if (setresuid(AGENT_UID, AGENT_UID, AGENT_UID) != 0) {
        fail("could not assume the agent user");
    }

    /*
     * Verify rather than assume. A saved UID left at 0 would let the target
     * program walk straight back to root, which would defeat the entire design.
     */
    uid_t real_uid = 0, effective_uid = 0, saved_uid = 0;
    gid_t real_gid = 0, effective_gid = 0, saved_gid = 0;
    if (getresuid(&real_uid, &effective_uid, &saved_uid) != 0
        || getresgid(&real_gid, &effective_gid, &saved_gid) != 0) {
        fail("could not read the resulting identity");
    }
    if (real_uid != AGENT_UID || effective_uid != AGENT_UID || saved_uid != AGENT_UID
        || real_gid != AGENT_GID || effective_gid != AGENT_GID || saved_gid != AGENT_GID) {
        fail("agent identity verification failed");
    }
    if (setuid(0) == 0 || seteuid(0) == 0) {
        fail("privilege drop was reversible");
    }
    if (getgroups(0, NULL) != 0) {
        fail("supplementary groups survived the privilege drop");
    }

    umask(0077);
}

static void require_controller_caller(void)
{
    /*
     * Mode 4750 root:control already denies the agent identity. This is the
     * second, independent check so a mis-set mode cannot silently widen access.
     */
    if (getuid() != CONTROL_UID) {
        fail("only the controller identity may invoke this launcher");
    }
}

/*
 * Enters the requested directory and then verifies where we actually landed.
 *
 * is_agent_directory() only bounds the text of the request. The path is resolved
 * under the agent identity, which owns /data/home and /data/workspace, so a
 * symlink planted inside the tree could point anywhere. getcwd() after the chdir
 * reports the resolved location, and requiring that to be under the agent data
 * root as well closes the gap: a launch whose working directory escaped the tree
 * is refused instead of started.
 *
 * "/" is the deliberate exception for the tmux operation: tmux does not take a
 * working directory from us (its panes use `-c`), so it starts from a neutral
 * root rather than a directory the agent can influence.
 */
static void enter_agent_directory(const char *directory)
{
    if (directory == NULL) {
        return;
    }
    if (chdir(directory) != 0) {
        fail("could not enter the requested directory");
    }
    if (strcmp(directory, "/") == 0) {
        return;
    }

    char resolved[PATH_MAX];
    if (getcwd(resolved, sizeof(resolved)) == NULL) {
        fail("could not resolve the working directory");
    }
    if (!is_agent_directory(resolved)) {
        fail("the working directory resolved outside the agent data root");
    }
}

static void exec_as_agent(const char *program, char *const argv[], const char *directory)
{
    /* Built before the environment is rebuilt from the caller's, and before the
     * descriptors it may need are closed. */
    char **environment = build_environment();

    close_inherited_descriptors();
    become_agent();

    if (setpgid(0, 0) != 0) {
        fail("could not start a new process group");
    }

    /* After become_agent(): the resolution must be governed by the identity that
     * will use the directory, not by the elevated one. */
    enter_agent_directory(directory);

    execve(program, argv, environment);
    fail("could not execute the requested program");
}

static int operation_acp(int argc, char *argv[])
{
    if (argc != 5) {
        fail("usage: acp <hostname> <port> <cwd>");
    }

    const char *hostname = argv[2];
    const char *port = argv[3];
    const char *cwd = argv[4];

    if (!is_loopback_hostname(hostname)) {
        fail("the ACP hostname must be a loopback address");
    }
    if (!is_port(port)) {
        fail("the ACP port must be between 1 and 65535");
    }
    if (!is_agent_directory(cwd)) {
        fail("the ACP working directory must be an agent data directory");
    }

    char *const child[] = {
        (char *)"opencode", (char *)"acp",
        (char *)"--hostname", (char *)hostname,
        (char *)"--port", (char *)port,
        (char *)"--cwd", (char *)cwd,
        NULL,
    };
    exec_as_agent(OPENCODE_PATH, child, cwd);
    return 64;
}

static int operation_tmux(int argc, char *argv[])
{
    if (argc < 3) {
        fail("usage: tmux <subcommand> [arguments...]");
    }
    if (argc > MAX_ARGUMENTS) {
        fail("too many tmux arguments");
    }
    if (!string_in(argv[2], kTmuxSubcommands, sizeof(kTmuxSubcommands) / sizeof(kTmuxSubcommands[0]))) {
        fail("unsupported tmux subcommand");
    }

    char *child[MAX_ARGUMENTS + 2];
    size_t used = 0;
    child[used++] = (char *)"tmux";
    for (int index = 2; index < argc; index++) {
        if (!is_bounded(argv[index])) {
            fail("tmux argument exceeds the supported size");
        }
        child[used++] = argv[index];
    }
    child[used] = NULL;

    /*
     * tmux arguments stay caller-defined on purpose: they are targets, formats
     * and pane command vectors. A pane command is an arbitrary program, so this
     * operation does let the controller execute code of its choosing - but only
     * ever as the agent identity, which it already drives. The bound that
     * matters is the fixed binary, the allow-listed subcommand and the
     * mandatory, verified privilege drop below.
     */
    exec_as_agent(TMUX_PATH, child, "/");
    return 64;
}

static int operation_pty(int argc, char *argv[])
{
    if (argc != 4) {
        fail("usage: pty <home> <session>");
    }

    const char *home = argv[2];
    const char *session = argv[3];

    if (!is_agent_directory(home)) {
        fail("the bridge home must be an agent data directory");
    }
    if (!is_session_name(session)) {
        fail("invalid tmux session name");
    }

    char *const child[] = {
        (char *)"python3", (char *)"-u", (char *)BRIDGE_PATH, (char *)session, NULL,
    };
    exec_as_agent(PYTHON_PATH, child, home);
    return 64;
}

/* Reads PPid and the four Uid fields from /proc/<pid>/status. */
static int read_process_identity(pid_t pid, pid_t *parent, uid_t identity[4])
{
    char path[64];
    snprintf(path, sizeof(path), "/proc/%d/status", (int)pid);

    FILE *status = fopen(path, "r");
    if (status == NULL) {
        return 0;
    }

    int found_parent = 0;
    int found_uid = 0;
    char line[512];
    while (fgets(line, sizeof(line), status) != NULL) {
        if (strncmp(line, "PPid:", 5) == 0) {
            *parent = (pid_t)strtol(line + 5, NULL, 10);
            found_parent = 1;
        } else if (strncmp(line, "Uid:", 4) == 0) {
            unsigned long real = 0, effective = 0, saved = 0, filesystem = 0;
            if (sscanf(line + 4, "%lu %lu %lu %lu", &real, &effective, &saved, &filesystem) != 4) {
                fclose(status);
                return 0;
            }
            identity[0] = (uid_t)real;
            identity[1] = (uid_t)effective;
            identity[2] = (uid_t)saved;
            identity[3] = (uid_t)filesystem;
            found_uid = 1;
        }
    }

    fclose(status);
    return found_parent && found_uid;
}

static int operation_signal(int argc, char *argv[])
{
    if (argc != 4) {
        fail("usage: signal <pid> TERM|KILL");
    }
    if (!is_digits(argv[2], 10)) {
        fail("the target pid must be numeric");
    }

    long parsed = strtol(argv[2], NULL, 10);
    if (parsed <= 1) {
        fail("the target pid is out of range");
    }
    pid_t target = (pid_t)parsed;

    int number;
    if (strcmp(argv[3], "TERM") == 0) {
        number = SIGTERM;
    } else if (strcmp(argv[3], "KILL") == 0) {
        number = SIGKILL;
    } else {
        fail("only TERM and KILL are supported");
        return 64;
    }

    pid_t parent = 0;
    uid_t identity[4] = {0, 0, 0, 0};
    if (!read_process_identity(target, &parent, identity)) {
        /* Already gone or unreadable: never guess, and never signal blindly. */
        return 1;
    }

    /*
     * The target must be a sibling child of the process that invoked us, i.e. a
     * child of the controller, and must be fully the agent identity. Nothing
     * else in the container can be signalled through this interface.
     */
    if (parent != getppid()) {
        fail("the target is not a child of the calling controller");
    }
    for (size_t index = 0; index < 4; index++) {
        if (identity[index] != AGENT_UID) {
            fail("the target is not an agent process");
        }
    }

    /*
     * PID reuse between the check above and the kill below is the classic
     * concern. It does not apply here, for reasons that hold on both sides:
     *
     *  - The caller (AgentProcessLauncher.TryTerminate) reads the PID from a
     *    live Process object it still owns and has not waited on, so the PID
     *    cannot have been released, let alone recycled.
     *  - Even if it somehow were, the replacement would have to be a child of
     *    this exact controller process AND fully the agent identity to pass the
     *    checks - and the only processes satisfying both are the ones this
     *    interface is allowed to signal anyway.
     *
     * The worst case is therefore signalling a different agent child of the same
     * controller, never an unrelated or more privileged process.
     */

    /* Launched children lead their own process group (see exec_as_agent). */
    int group = kill(-target, number);
    int single = kill(target, number);
    if (group != 0 && single != 0) {
        return errno == ESRCH ? 1 : 2;
    }
    return 0;
}

int main(int argc, char *argv[])
{
    if (argc < 2) {
        fail("usage: agentcontrol-launch <acp|tmux|pty|signal> [arguments...]");
    }
    if (argc > MAX_ARGUMENTS) {
        fail("too many arguments");
    }
    for (int index = 0; index < argc; index++) {
        if (!is_bounded(argv[index])) {
            fail("argument exceeds the supported size");
        }
    }

    require_controller_caller();

    const char *operation = argv[1];
    if (strcmp(operation, "acp") == 0) {
        return operation_acp(argc, argv);
    }
    if (strcmp(operation, "tmux") == 0) {
        return operation_tmux(argc, argv);
    }
    if (strcmp(operation, "pty") == 0) {
        return operation_pty(argc, argv);
    }
    if (strcmp(operation, "signal") == 0) {
        return operation_signal(argc, argv);
    }

    fail("unsupported operation");
    return 64;
}
