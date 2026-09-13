#!/usr/bin/env python3
"""PTY bridge for the AgentControl browser terminal.

The bridge owns a pseudo-terminal that runs exactly one client:

    tmux attach-session -t <target>

It speaks newline-delimited JSON with the .NET controller on stdin/stdout:

    stdin : {"type":"input","data":"..."}
            {"type":"resize","cols":N,"rows":N}
    stdout: {"type":"output","data":"..."}
            {"type":"error","message":"..."}

There is deliberately no fallback shell: if the tmux session is missing the
attach client exits and the bridge reports the failure. There is no network
listener. When the controller closes stdin the bridge terminates and reaps only
its own tmux attach client; the tmux server, TUI and ACP runtime keep running.
"""

from __future__ import annotations

import codecs
import fcntl
import json
import os
import pty
import select
import struct
import subprocess
import sys
import termios

MIN_COLS = 20
MAX_COLS = 300
MIN_ROWS = 5
MAX_ROWS = 100
DEFAULT_COLS = 80
DEFAULT_ROWS = 24

MAX_INPUT_CHARS = 16 * 1024
MAX_LINE_BYTES = 512 * 1024
MAX_WRITE_BYTES = 256 * 1024
READ_SIZE = 65536

TMUX_PROGRAM = "tmux"


def clamp_size(cols, rows):
    """Clamp a terminal size into the controller's supported bounds."""
    try:
        cols = int(cols)
    except (TypeError, ValueError):
        cols = DEFAULT_COLS
    try:
        rows = int(rows)
    except (TypeError, ValueError):
        rows = DEFAULT_ROWS
    return (min(max(cols, MIN_COLS), MAX_COLS), min(max(rows, MIN_ROWS), MAX_ROWS))


def parse_command(line):
    """Parse one controller stdin line into a normalized command, or None."""
    if not isinstance(line, str) or not line:
        return None
    try:
        message = json.loads(line)
    except (ValueError, TypeError):
        return None
    if not isinstance(message, dict):
        return None

    kind = message.get("type")
    if kind == "input":
        data = message.get("data")
        if not isinstance(data, str) or len(data) > MAX_INPUT_CHARS:
            return None
        return {"type": "input", "data": data}

    if kind == "resize":
        cols = message.get("cols")
        rows = message.get("rows")
        if not _is_int(cols) or not _is_int(rows):
            return None
        cols, rows = clamp_size(cols, rows)
        return {"type": "resize", "cols": cols, "rows": rows}

    return None


def _is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def attach_failure_message(returncode):
    """Return a generic error for a failed tmux attach, or None on success."""
    if returncode is None or returncode == 0:
        return None
    if returncode < 0:
        return "tmux attach was terminated by signal %d." % (-returncode)
    return "tmux attach exited with status %d." % returncode


def _encode_frame(payload):
    return (json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


class Bridge:
    def __init__(self, target):
        self.target = target
        self.master_fd = None
        self.process = None
        self.decoder = codecs.getincrementaldecoder("utf-8")("replace")
        self.stdin_buffer = bytearray()
        self.write_buffer = bytearray()
        self._stdout = sys.stdout.buffer

    def start(self):
        master_fd, slave_fd = pty.openpty()
        os.set_blocking(master_fd, False)
        self._set_winsize(master_fd, DEFAULT_COLS, DEFAULT_ROWS)

        def _make_controlling_terminal():
            # subprocess has already duplicated the slave onto 0/1/2 here.
            os.setsid()
            fcntl.ioctl(0, termios.TIOCSCTTY, 0)

        try:
            self.process = subprocess.Popen(
                [TMUX_PROGRAM, "attach-session", "-t", self.target],
                stdin=slave_fd,
                stdout=slave_fd,
                stderr=slave_fd,
                close_fds=True,
                preexec_fn=_make_controlling_terminal,
            )
        finally:
            os.close(slave_fd)
        self.master_fd = master_fd

    def run(self):
        self.start()
        viewer_closed = False
        try:
            while True:
                writable = [self.master_fd] if self.write_buffer else []
                readable, _, _ = select.select([0, self.master_fd], writable, [], 0.5)
                if not self._flush_writes():
                    break
                if 0 in readable and not self._handle_stdin():
                    # Controller closed stdin: this is a viewer detach, not an
                    # attach failure, so no error frame is emitted.
                    viewer_closed = True
                    break
                if self.master_fd in readable and not self._handle_master():
                    break
                if self._child_exited():
                    break
        finally:
            if not viewer_closed:
                self._drain_master()
                failure = attach_failure_message(self.process.poll() if self.process is not None else None)
                if failure is not None:
                    self._emit_error(failure)
            tail = self.decoder.decode(b"", final=True)
            if tail:
                self._emit_output(tail)
            self.cleanup()

    def cleanup(self):
        """Close the PTY and terminate/reap only this bridge's attach client."""
        if self.master_fd is not None:
            try:
                os.close(self.master_fd)
            except OSError:
                pass
            self.master_fd = None

        if self.process is None:
            return

        if self.process.poll() is None:
            try:
                self.process.terminate()
            except OSError:
                pass
            try:
                self.process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                try:
                    self.process.kill()
                except OSError:
                    pass
                try:
                    self.process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    pass

        try:
            self.process.wait(timeout=0)
        except (subprocess.TimeoutExpired, OSError):
            pass

    def _set_winsize(self, fd, cols, rows):
        cols, rows = clamp_size(cols, rows)
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
        return cols, rows

    def _write_frame(self, payload):
        try:
            self._stdout.write(_encode_frame(payload))
            self._stdout.flush()
        except (BrokenPipeError, OSError):
            raise SystemExit(0)

    def _emit_output(self, data):
        self._write_frame({"type": "output", "data": data})

    def _emit_error(self, message):
        self._write_frame({"type": "error", "message": message})

    def _handle_command(self, command):
        if command["type"] == "input":
            data = command["data"].encode("utf-8")
            if not data:
                return
            if len(self.write_buffer) + len(data) > MAX_WRITE_BYTES:
                self._emit_error("Terminal input backlog exceeds the limit.")
                return
            self.write_buffer.extend(data)
        elif command["type"] == "resize":
            self._set_winsize(self.master_fd, command["cols"], command["rows"])

    def _flush_writes(self):
        while self.write_buffer:
            try:
                written = os.write(self.master_fd, self.write_buffer)
            except BlockingIOError:
                return True
            except OSError:
                return False
            if written <= 0:
                return True
            del self.write_buffer[:written]
        return True

    def _handle_stdin(self):
        try:
            chunk = os.read(0, READ_SIZE)
        except BlockingIOError:
            return True
        except OSError:
            return False
        if not chunk:
            return False

        self.stdin_buffer.extend(chunk)
        while True:
            newline = self.stdin_buffer.find(b"\n")
            if newline < 0:
                if len(self.stdin_buffer) > MAX_LINE_BYTES:
                    self._emit_error("Terminal command exceeds the limit.")
                    self.stdin_buffer.clear()
                break
            raw = bytes(self.stdin_buffer[:newline])
            del self.stdin_buffer[: newline + 1]
            line = raw.decode("utf-8", "replace").rstrip("\r")
            if not line:
                continue
            command = parse_command(line)
            if command is None:
                self._emit_error("Unsupported terminal command.")
                continue
            self._handle_command(command)
        return True

    def _handle_master(self):
        try:
            data = os.read(self.master_fd, READ_SIZE)
        except BlockingIOError:
            return True
        except OSError:
            return False
        if not data:
            return False
        text = self.decoder.decode(data)
        if text:
            self._emit_output(text)
        return True

    def _drain_master(self):
        while True:
            try:
                data = os.read(self.master_fd, READ_SIZE)
            except (BlockingIOError, OSError):
                break
            if not data:
                break
            text = self.decoder.decode(data)
            if text:
                self._emit_output(text)

    def _child_exited(self):
        return self.process is not None and self.process.poll() is not None


def main(argv):
    if len(argv) != 2 or not argv[1]:
        sys.stderr.write("usage: pty_bridge.py <tmux-target>\n")
        return 2
    try:
        Bridge(argv[1]).run()
    except KeyboardInterrupt:
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
