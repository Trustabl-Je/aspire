"""Independent, branch-only recovery for the baseline investigation testbed.

The Git journal is separate from production checkpoints and uses blob-SHA
compare-and-swap updates. Lost dispatch receipts require manual reconciliation;
lost cancellation receipts are observed, never blindly retried.

Deadlines include queue time: 70 minutes for agents, 75 for matrix workers,
12 hours for the investigation. The five-hour supervisor can be manually
restarted with the same enrollment/journal if it dies. It is not a permanent
default-branch scheduled recovery service.
"""

import base64
from datetime import datetime, timezone
import json
import os
import re
import subprocess
import time


REPOSITORY = "microsoft/aspire"
WORKFLOW = ".github/workflows/reproduce-flaky-tests.yml"


def epoch(value):
    return datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp()


def api(endpoint, payload=None, method=None, optional=False):
    command = ["gh", "api", f"repos/{REPOSITORY}/{endpoint}"]
    if method:
        command += ["--method", method]
    if payload is not None:
        command += ["--input", "-"]
    result = subprocess.run(
        command, input=json.dumps(payload) if payload is not None else None,
        capture_output=True, text=True, timeout=120, check=False,
    )
    if result.returncode:
        if optional and "(HTTP 404)" in result.stderr:
            return None
        raise RuntimeError(result.stderr)
    return json.loads(result.stdout) if result.stdout else None


class Recovery:
    def __init__(self, enrollment, call=api, now=time.time):
        self.enrollment, self.call, self.now = enrollment, call, now
        self.issue = enrollment["issue_url"].rsplit("/", 1)[-1]
        if not re.fullmatch(r"[1-9][0-9]*", self.issue):
            raise ValueError("Invalid issue")
        if enrollment["issue_url"] != f"https://github.com/{REPOSITORY}/issues/{self.issue}":
            raise ValueError("Invalid issue URL")
        self.branch = f"ankj/flaky-test-{self.issue}-recovery"
        self.document = None
        self.blob = None

    def checkpoint(self):
        reference = self.call(f"git/ref/heads/ankj/flaky-test-{self.issue}-state")
        revision = reference["object"]["sha"]
        document = self.call(f"contents/state.json?ref={revision}")
        state = json.loads(base64.b64decode(document["content"]))
        for key in ["issue_url", "control_sha", "agent_sha", "worker_sha"]:
            if state[key] != self.enrollment[key]:
                raise ValueError(f"Checkpoint identity changed: {key}")
        if state["agent_workflow"] != "reproduce-flaky-tests.yml":
            raise ValueError("Checkpoint workflow changed")
        return state, revision

    def load(self, initial_outer):
        state, _ = self.checkpoint()
        document = self.call(f"contents/recovery.json?ref={self.branch}", optional=True)
        if document is None:
            # Creation is deliberately separate from updating: an existing ref
            # without a journal is an uncertain initialization, not a fresh run.
            if self.call(f"git/ref/heads/{self.branch}", optional=True) is not None:
                raise ValueError("Recovery initialization is incomplete; inspect before retrying")
            self.outer(initial_outer)
            self.call("git/refs", {"ref": f"refs/heads/{self.branch}",
                                  "sha": self.enrollment["control_sha"]}, method="POST")
            self.document = {
                "version": 1, "enrollment": self.enrollment,
                "deadline": epoch(state["created_at"]) + 12 * 60 * 60,
                "outer": initial_outer, "resumes": 0, "children": {},
                "pending_dispatch": None, "cancellations": {}, "status": "active",
            }
            self.save()
        else:
            self.blob = document["sha"]
            self.document = json.loads(base64.b64decode(document["content"]))
            if (self.document["version"] != 1
                    or self.document["enrollment"] != self.enrollment
                    or self.document["deadline"] != epoch(state["created_at"]) + 12 * 60 * 60):
                raise ValueError("Recovery journal identity or deadline changed")

    def save(self):
        payload = {
            "branch": self.branch,
            "message": "test(ci): checkpoint bounded investigation recovery\n\n"
                       "Co-authored-by: Copilot App <223556219+Copilot@users.noreply.github.com>",
            "content": base64.b64encode(
                (json.dumps(self.document, indent=2) + "\n").encode()).decode(),
        }
        if self.blob:
            payload["sha"] = self.blob
        result = self.call("contents/recovery.json", payload, method="PUT")
        self.blob = result["content"]["sha"]

    def run(self, run_id, sha, branch):
        run = self.call(f"actions/runs/{run_id}")
        if (run["id"] != run_id or run["head_sha"] != sha
                or run["head_branch"] != branch
                or run["path"].split("@", 1)[0] != WORKFLOW
                or run["event"] != "workflow_dispatch"
                or run["run_attempt"] != 1
                or run["repository"]["full_name"] != REPOSITORY):
            raise ValueError("Owned run identity changed")
        return run

    def outer(self, run_id):
        return self.run(run_id, self.enrollment["control_sha"],
                        self.enrollment["control_branch"])

    def child(self, state):
        if state["phase"] not in {"agent", "worker"}:
            return None
        owned = state[state["phase"]]
        expected = self.enrollment[f"{state['phase']}_sha"]
        if (owned["sha"] != expected
                or owned["workflow"] != "reproduce-flaky-tests.yml"
                or not owned["branch"].startswith(f"ankj/flaky-test-{self.issue}-")):
            raise ValueError("Checkpoint child identity changed")
        run = self.run(owned["id"], expected, owned["branch"])
        key = str(run["id"])
        # Absolute deadlines derive from GitHub creation time, including queue
        # delay. Neither a new outer run nor supervisor restart resets them.
        deadline = epoch(run["created_at"]) + (70 if state["phase"] == "agent" else 75) * 60
        record = {"sha": expected, "branch": owned["branch"], "deadline": deadline}
        previous = self.document["children"].get(key)
        if previous is not None and previous != record:
            raise ValueError("Recorded child deadline or identity changed")
        if previous is None:
            self.document["children"][key] = record
            self.save()
        return run, deadline

    def cancel(self, run):
        if run["status"] == "completed":
            return True
        key = str(run["id"])
        requested = self.document["cancellations"].get(key)
        if requested is None:
            self.document["cancellations"][key] = self.now()
            self.save()
            # A lost receipt is not retried. Recovery observes actual run status
            # and leaves a visible blocker if cancellation never completes.
            self.call(f"actions/runs/{run['id']}/cancel", {}, method="POST")
        elif self.now() - requested > 10 * 60:
            raise TimeoutError(f"Cancellation of {key} is not confirmed after ten minutes")
        return False

    def require_reconciled(self, state, revision):
        if state["phase"] not in {"agent", "worker", "ready", "candidate-ready", "terminal"}:
            self.document.update(
                status="blocked", reason="Uncertain controller intent; reconcile owned dispatch",
                blocked_checkpoint=revision,
            )
            self.save()
            raise ValueError("Uncertain controller intent; explicit reconciliation required")

    def tick(self):
        journal = self.document
        if journal["pending_dispatch"] is not None:
            raise ValueError("Uncertain resume dispatch; reconcile its receipt before retrying")
        if journal["status"] not in {"active", "expiring"}:
            return True
        outer = self.outer(journal["outer"])
        state, revision = self.checkpoint()
        child = self.child(state)
        expired = (self.now() >= journal["deadline"]
                   or child is not None and child[0]["status"] != "completed"
                   and self.now() >= child[1])
        if expired or journal["status"] == "expiring":
            if journal["status"] != "expiring":
                journal.update(status="expiring", expiry_checkpoint=revision)
                self.save()
            # Stop the controller first so it cannot consume a result or
            # dispatch another child concurrently with cancellation.
            if not self.cancel(outer):
                return False
            state, revision = self.checkpoint()
            self.require_reconciled(state, revision)
            child = self.child(state)
            if child is not None and not self.cancel(child[0]):
                return False
            journal["status"] = "timed-out"
            self.save()
            return True
        if outer["status"] != "completed":
            return False
        self.require_reconciled(state, revision)
        if journal.get("last_resume_checkpoint") == revision:
            journal.update(status="blocked",
                           reason="Resumed controller made no checkpoint progress",
                           blocked_outer=outer["id"], blocked_checkpoint=revision)
            self.save()
            raise ValueError("Resumed controller made no checkpoint progress; inspect its failure")
        if state["phase"] == "terminal":
            journal["status"] = "terminal"
            self.save()
            return True
        if (any(item["phase"] == "reproduce" and item["failed"] > 0
                for item in state.get("history", []))):
            if child is not None and child[0]["status"] != "completed":
                return False
            journal["status"] = "baseline-paused"
            self.save()
            return True
        if child is not None and child[0]["status"] != "completed":
            return False
        if journal["resumes"] >= 12:
            raise ValueError("Recovery resume budget exhausted")
        if self.checkpoint()[1] != revision:
            raise ValueError("Checkpoint advanced before recovery; refusing duplicate resume")
        journal["pending_dispatch"] = {"checkpoint": revision, "at": self.now()}
        self.save()
        receipt = self.call("actions/workflows/reproduce-flaky-tests.yml/dispatches", {
            "ref": self.enrollment["control_branch"],
            "inputs": {"issue_url": self.enrollment["issue_url"], "operation": "resume"},
            "return_run_details": True,
        }, method="POST")
        run_id = receipt.get("workflow_run_id") if isinstance(receipt, dict) else None
        if not isinstance(run_id, int) or isinstance(run_id, bool):
            raise ValueError("Resume dispatch has no exact receipt; do not retry")
        self.outer(run_id)
        journal.update(outer=run_id, resumes=journal["resumes"] + 1,
                       last_resume_checkpoint=revision, pending_dispatch=None)
        self.save()
        return False


def main():
    if os.environ["GITHUB_REPOSITORY"] != REPOSITORY:
        raise ValueError("Recovery is restricted to the enrolled upstream trial")
    enrollment = json.loads(os.environ["RECOVERY_ENROLLMENT"])
    recovery = Recovery(enrollment)
    recovery.load(int(os.environ["RECOVERY_INITIAL_OUTER"]))
    # This independent job is bounded too. A later manual invocation loads the
    # same journal; it never creates fresh child or investigation budgets.
    deadline = time.monotonic() + 5 * 60 * 60
    while time.monotonic() < deadline:
        done = recovery.tick()
        print(json.dumps({"recovery": recovery.document}), flush=True)
        if done:
            if recovery.document["status"] in {"timed-out", "blocked"}:
                raise RuntimeError(f"Recovery stopped: {recovery.document['status']}; inspect journal")
            return
        time.sleep(30)
    raise TimeoutError("Recovery supervisor budget expired; resume this journal explicitly")


if __name__ == "__main__":
    main()
