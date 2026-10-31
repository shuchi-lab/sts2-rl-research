"""Bound game-launched inference to the lifetime of its native parent process."""
from __future__ import annotations

import os
import threading

import psutil


def game_parent() -> psutil.Process | None:
    """The mod starts Python directly; a terminal launch has a shell parent instead."""
    try:
        parent = psutil.Process(os.getppid())
        if parent.name().lower().replace(' ', '') in ('slaythespire2', 'slaythespire2.exe'):
            return parent
    except (psutil.NoSuchProcess, psutil.AccessDenied):
        pass
    return None


class ParentLifetime:
    def __init__(self, stop: threading.Event, parent: psutil.Process | None, *, poll_seconds=.5):
        self.stop, self.parent, self.poll_seconds = stop, parent, poll_seconds
        self.parent_exited = threading.Event()
        self.thread = None

    def start(self) -> None:
        if self.parent is not None:
            self.thread = threading.Thread(target=self._watch, name='game-parent-lifetime', daemon=True)
            self.thread.start()

    def _watch(self) -> None:
        while not self.stop.wait(self.poll_seconds):
            try:
                # is_running also detects PID reuse; a terminated, unreaped parent
                # can still have a PID, so treat zombies as exited too.
                alive = self.parent.is_running() and self.parent.status() not in (
                    psutil.STATUS_ZOMBIE, psutil.STATUS_DEAD)
            except psutil.NoSuchProcess:
                alive = False
            except psutil.AccessDenied:
                continue
            if not alive:
                self.parent_exited.set()
                self.stop.set()
                return

    def close(self) -> None:
        self.stop.set()
        if self.thread is not None:
            self.thread.join(timeout=2)
