"""Branch-only outer loop; task policy stays in the production controller."""

import base64
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time


REPOSITORY = "microsoft/aspire"
ISSUE_URL = os.environ["ISSUE_URL"]
MATCH = re.fullmatch(r"https://github\.com/microsoft/aspire/issues/([1-9][0-9]*)", ISSUE_URL)
if not MATCH or os.environ["GITHUB_REPOSITORY"] != REPOSITORY:
    raise ValueError("The hosted validation accepts only an exact upstream issue URL")
ISSUE = MATCH[1]


def api(endpoint, payload=None, optional=False, deadline=None):
    command = ["gh", "api", f"repos/{REPOSITORY}/{endpoint}"]
    if payload is not None:
        command += ["--method", "POST", "--input", "-"]
    timeout = min(120, deadline - time.monotonic()) if deadline is not None else 120
    if timeout <= 0:
        raise TimeoutError("API budget expired; checkpoint retained for explicit recovery")
    try:
        result = subprocess.run(command, input=json.dumps(payload) if payload else None,
                                text=True, capture_output=True, check=True, timeout=timeout)
    except subprocess.CalledProcessError as error:
        if optional and "(HTTP 404)" in (error.stderr or ""):
            return None
        raise
    if deadline is not None and time.monotonic() >= deadline:
        raise TimeoutError("API budget expired; checkpoint retained for explicit recovery")
    return json.loads(result.stdout) if result.stdout else None


def checkpoint(optional=False, deadline=None):
    reference = api(f"git/ref/heads/ankj/flaky-test-{ISSUE}-state",
                    optional=optional, deadline=deadline)
    if reference is None:
        return None
    revision = reference["object"]["sha"]
    document = api(f"contents/state.json?ref={revision}", deadline=deadline)
    state = json.loads(base64.b64decode(document["content"]))
    if (state["issue_url"] != ISSUE_URL
            or state["control_sha"] != os.environ["GITHUB_SHA"]
            or state["agent_sha"] != os.environ["FLAKY_AGENT_SHA"]
            or state["worker_sha"] != os.environ["FLAKY_WORKER_SHA"]
            or state["agent_workflow"] != "reproduce-flaky-tests.yml"):
        raise ValueError("Checkpoint changed its enrolled investigation identity")
    return state, revision


def pause_at_recorded_failure(state, revision):
    if (os.environ.get("FLAKY_PROOF_BASELINE_ONLY") == "true"
            and state["phase"] != "terminal"
            and any(item["phase"] == "reproduce" and item["failed"] > 0
                    for item in state.get("history", []))):
        print(json.dumps({"proof_paused": {
            "checkpoint": revision, "phase": state["phase"],
            "reason": "Controller recorded a target failure; repair safety gates "
                      "remain unresolved. No further advancement is attempted.",
        }}), flush=True)
        if summary := os.environ.get("GITHUB_STEP_SUMMARY"):
            with Path(summary).open("a", encoding="utf-8") as output:
                output.write("\n### Premerge proof paused: baseline failure recorded\n\n"
                             "Repair safety gates remain unresolved. No further advancement "
                             "is attempted; this is not a verified repair or a terminal "
                             "investigation finding. The checkpoint is retained.\n")
        sys.exit(0)


def wait_for_owned_run(owned, deadline):
    # Only this non-model step waits, using the recorded run rather than a
    # "latest run" search. One deadline covers the whole outer job, not each child.
    while True:
        if time.monotonic() >= deadline:
            raise TimeoutError("Outer job budget expired; checkpoint retained for explicit recovery")
        run = api(f"actions/runs/{owned['id']}", deadline=deadline)
        if (run["id"] != owned["id"] or run["head_sha"] != owned["sha"]
                or run["path"].split("@", 1)[0].rsplit("/", 1)[-1]
                != owned["workflow"].rsplit("/", 1)[-1]):
            raise ValueError("Owned run identity changed while awaiting completion")
        if run["status"] == "completed":
            print(json.dumps({"owned_run": run["id"], "conclusion": run["conclusion"]}),
                  flush=True)
            break
        print(json.dumps({"waiting_for": run["html_url"],
                          "status": run["status"]}), flush=True)
        time.sleep(min(30, max(0, deadline - time.monotonic())))


def advance_controller(operation, deadline):
    command = [sys.executable, ".github/scripts/flaky_investigation/controller.py", "advance"]
    # This testbed runs on Ubuntu. Give only this controller and its local API
    # children a process group, so timeout cleanup cannot kill unrelated jobs.
    with subprocess.Popen(command, env={**os.environ, "OPERATION": operation},
                          start_new_session=True) as process:
        try:
            code = process.wait(timeout=max(0, deadline - time.monotonic()))
        except subprocess.TimeoutExpired as error:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
            raise TimeoutError("Controller budget expired; checkpoint intents require explicit recovery") from error
        if code:
            raise subprocess.CalledProcessError(code, command)
    if time.monotonic() >= deadline:
        raise TimeoutError("Outer job budget expired; checkpoint retained for explicit recovery")


def drive():
    deadline = time.monotonic() + 60 * 60
    operation = os.environ.get("OPERATION", "resume")
    expected_revision = None
    completed_runs = set()
    existing = checkpoint(optional=operation == "start", deadline=deadline)
    if existing is not None:
        state, expected_revision = existing
        if (operation != "cancel" and os.environ.get("FLAKY_PROOF_BASELINE_ONLY") == "true"
                and any(item["phase"] == "reproduce" and item["failed"] > 0
                        for item in state.get("history", []))):
            if state["phase"] in {"agent", "worker"}:
                wait_for_owned_run(state[state["phase"]], deadline)
                current, revision = checkpoint(deadline=deadline)
                if revision != expected_revision:
                    raise ValueError("Checkpoint advanced elsewhere; refusing duplicate advancement")
                state = current
            pause_at_recorded_failure(state, expected_revision)
    for _ in range(32):
        if time.monotonic() >= deadline:
            raise TimeoutError("Outer job budget expired; checkpoint retained for explicit recovery")
        advance_controller(operation, deadline)
        state, revision = checkpoint(deadline=deadline)
        print(json.dumps({"checkpoint": revision, "phase": state["phase"],
                          "outcome": state.get("outcome")}), flush=True)
        if state["phase"] == "terminal":
            return
        if state["phase"] not in {"agent", "worker", "ready", "candidate-ready"}:
            raise ValueError("Uncertain checkpoint; refusing automatic redispatch")
        owned = state.get(state["phase"]) if state["phase"] in {"agent", "worker"} else None
        if owned is not None:
            if owned["id"] in completed_runs:
                raise ValueError("Controller retained an already completed child; explicit recovery required")
            wait_for_owned_run(owned, deadline)
            completed_runs.add(owned["id"])
        elif revision == expected_revision:
            raise ValueError("Controller made no checkpoint progress; explicit recovery required")
        current, current_revision = checkpoint(deadline=deadline)
        if current_revision != revision:
            raise ValueError("Checkpoint advanced elsewhere; refusing duplicate advancement")
        pause_at_recorded_failure(current, current_revision)
        expected_revision = current_revision
        operation = "resume"
    raise RuntimeError("Outer loop exceeded its bounded advances; checkpoint retained for explicit recovery")


if __name__ == "__main__":
    drive()
