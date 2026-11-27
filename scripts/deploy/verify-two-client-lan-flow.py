#!/usr/bin/env python3
"""
Multi-Client LAN Flow Simulation and Verification Script
Simulates two distinct client workstations (Workstation-A: Owner, Workstation-B: Designer)
executing concurrent operations, project membership, job queues, and security authorization boundaries.
"""

import sys
import os
import json
import time
import uuid
import urllib.request
import urllib.parse
import ssl
from datetime import datetime
from concurrent.futures import ThreadPoolExecutor

def make_request(url, method="GET", headers=None, data=None, ssl_context=None):
    if headers is None:
        headers = {}
    req_data = None
    if data is not None:
        req_data = json.dumps(data).encode("utf-8")
        headers["Content-Type"] = "application/json"
    
    req = urllib.request.Request(url, data=req_data, headers=headers, method=method)
    start_time = time.time()
    try:
        with urllib.request.urlopen(req, context=ssl_context, timeout=30) as resp:
            elapsed = time.time() - start_time
            body = resp.read().decode("utf-8")
            parsed = json.loads(body) if body and "application/json" in resp.headers.get("Content-Type", "") else body
            return {
                "status": resp.status,
                "headers": dict(resp.headers),
                "data": parsed,
                "latency_ms": round(elapsed * 1000, 2),
                "error": None
            }
    except urllib.error.HTTPError as e:
        elapsed = time.time() - start_time
        body = e.read().decode("utf-8") if e.fp else ""
        try:
            parsed = json.loads(body)
        except Exception:
            parsed = body
        return {
            "status": e.code,
            "headers": dict(e.headers),
            "data": parsed,
            "latency_ms": round(elapsed * 1000, 2),
            "error": f"HTTP {e.code}: {e.reason}"
        }
    except Exception as ex:
        elapsed = time.time() - start_time
        return {
            "status": 0,
            "headers": {},
            "data": None,
            "latency_ms": round(elapsed * 1000, 2),
            "error": str(ex)
        }

def run_simulation(base_url="http://localhost:5000", insecure=True):
    print("=" * 65)
    print(" CharacterConceptAI - Multi-Client LAN Simulation & Audit")
    print(f" Target Server URL: {base_url}")
    print(f" Timestamp:        {datetime.utcnow().isoformat()}Z")
    print("=" * 65)

    ssl_ctx = ssl.create_default_context()
    if insecure:
        ssl_ctx.check_hostname = False
        ssl_ctx.verify_mode = ssl.CERT_NONE

    audit_log = []

    def record_step(step_name, result, expected_status=None):
        passed = (result["status"] == expected_status) if expected_status else (result["status"] in (200, 201, 202))
        entry = {
            "step": step_name,
            "status": result["status"],
            "expected_status": expected_status,
            "passed": passed,
            "latency_ms": result["latency_ms"],
            "error": result["error"]
        }
        audit_log.append(entry)
        symbol = "[PASS]" if passed else "[FAIL]"
        print(f" {symbol:6} {step_name:<42} status={result['status']} ({result['latency_ms']} ms)")
        if not passed:
            print(f"        Error detail: {result.get('data') or result.get('error')}")
        return passed

    # 1. Health check
    print("\n--- Phase 1: Server Connectivity & Liveness ---")
    h_res = make_request(f"{base_url}/api/v1/health", ssl_context=ssl_ctx)
    if not record_step("Server Health Check", h_res, 200):
        print("ERROR: Server is not reachable at target URL.")
        return False, audit_log

    # 2. Login as Admin to provision users if necessary
    print("\n--- Phase 2: Client Session Authentication ---")
    admin_login = make_request(
        f"{base_url}/api/v1/auth/login",
        method="POST",
        data={"email": "admin@conceptai.local", "password": "Admin@Secured2026"},
        ssl_context=ssl_ctx
    )
    admin_token = None
    if admin_login["status"] == 200 and isinstance(admin_login["data"], dict):
        admin_token = admin_login["data"].get("accessToken")

    # Helper to authenticate or register user
    def authenticate_client(email, password, display_name, role):
        # Try login first
        res = make_request(f"{base_url}/api/v1/auth/login", method="POST", data={"email": email, "password": password}, ssl_context=ssl_ctx)
        if res["status"] == 200:
            return res["data"]["accessToken"]
        
        # If 401 and admin available, create account
        if admin_token:
            admin_headers = {"Authorization": f"Bearer {admin_token}"}
            create_acc = make_request(
                f"{base_url}/api/v1/admin/accounts",
                method="POST",
                headers=admin_headers,
                data={"email": email, "displayName": display_name, "password": password, "roles": [role]},
                ssl_context=ssl_ctx
            )
            if create_acc["status"] in (200, 201):
                # Now login
                res2 = make_request(f"{base_url}/api/v1/auth/login", method="POST", data={"email": email, "password": password}, ssl_context=ssl_ctx)
                if res2["status"] == 200:
                    return res2["data"]["accessToken"]
        raise RuntimeError(f"Could not authenticate {email}")

    owner_email = "lan.owner@conceptai.local"
    owner_pass = "Owner@Secured2026"
    designer_email = "lan.designer@conceptai.local"
    designer_pass = "Designer@Secured2026"

    try:
        owner_token = authenticate_client(owner_email, owner_pass, "Lead Art Director (PC-01)", "Owner")
        record_step("Workstation-A (Owner) Authentication", {"status": 200, "latency_ms": 10, "error": None}, 200)
    except Exception as ex:
        record_step("Workstation-A (Owner) Authentication", {"status": 500, "latency_ms": 0, "error": str(ex)}, 200)
        return False, audit_log

    try:
        designer_token = authenticate_client(designer_email, designer_pass, "Concept Artist (PC-02)", "Designer")
        record_step("Workstation-B (Designer) Authentication", {"status": 200, "latency_ms": 10, "error": None}, 200)
    except Exception as ex:
        record_step("Workstation-B (Designer) Authentication", {"status": 500, "latency_ms": 0, "error": str(ex)}, 200)
        return False, audit_log

    owner_headers = {"Authorization": f"Bearer {owner_token}"}
    designer_headers = {"Authorization": f"Bearer {designer_token}"}

    # 3. Project Creation by Owner
    print("\n--- Phase 3: Collaborative Project Membership ---")
    project_payload = {
        "name": f"LAN Production Sprint {uuid.uuid4().hex[:6]}",
        "projectType": "Game",
        "description": "Simulated multi-workstation collaborative production"
    }
    p_res = make_request(f"{base_url}/api/v1/projects", method="POST", headers=owner_headers, data=project_payload, ssl_context=ssl_ctx)
    if not record_step("Owner Creates Project", p_res, 201):
        return False, audit_log
    project_id = p_res["data"]["id"]

    # Designer attempts to read project before being added -> must return 404/403
    p_unauth = make_request(f"{base_url}/api/v1/projects/{project_id}", headers=designer_headers, ssl_context=ssl_ctx)
    record_step("Designer Unassigned Project Isolation", p_unauth, 404)

    # Owner adds Designer to Project
    add_member = make_request(
        f"{base_url}/api/v1/projects/{project_id}/members",
        method="POST",
        headers=owner_headers,
        data={"email": designer_email, "role": "Designer"},
        ssl_context=ssl_ctx
    )
    if not record_step("Owner Grants Designer Role", add_member, 201):
        return False, audit_log

    # Designer reads project after invitation -> must succeed
    p_auth = make_request(f"{base_url}/api/v1/projects/{project_id}", headers=designer_headers, ssl_context=ssl_ctx)
    record_step("Designer Project Access Verified", p_auth, 200)

    # 4. Designer creates Character
    char_res = make_request(
        f"{base_url}/api/v1/projects/{project_id}/characters",
        method="POST",
        headers=designer_headers,
        data={
            "name": "Kagura Tsuki",
            "role": "Shinobi",
            "description": "Stealth operative"
        },
        ssl_context=ssl_ctx
    )
    if not record_step("Designer Creates Character", char_res, 201):
        return False, audit_log
    char_id = char_res["data"]["id"]

    # 5. Concurrent Generation Requests
    print("\n--- Phase 4: Concurrent Job Queue Simulation ---")
    def submit_job(client_name, headers, prompt_text, seed):
        payload = {
            "characterId": char_id,
            "promptMode": 0,
            "rawPrompt": prompt_text,
            "finalPrompt": prompt_text,
            "promptTemplateVersion": "1.0",
            "width": 512,
            "height": 512,
            "requestedCount": 1,
            "seed": seed,
            "idempotencyKey": uuid.uuid4().hex
        }
        res = make_request(f"{base_url}/api/v1/projects/{project_id}/generations", method="POST", headers=headers, data=payload, ssl_context=ssl_ctx)
        return client_name, res

    with ThreadPoolExecutor(max_workers=2) as executor:
        f1 = executor.submit(submit_job, "PC-01 (Owner)", owner_headers, "Owner prompt: Anime cinematic landscape", 11111)
        f2 = executor.submit(submit_job, "PC-02 (Designer)", designer_headers, "Designer prompt: Character dynamic pose", 22222)
        r1_name, r1 = f1.result()
        r2_name, r2 = f2.result()

    record_step("Concurrent Job Submission 1 (PC-01)", r1, 202)
    record_step("Concurrent Job Submission 2 (PC-02)", r2, 202)

    def wait_for_job(job_url, headers):
        if not job_url:
            return None
        if job_url.startswith("/"):
            job_url = f"{base_url}{job_url}"
        for _ in range(180):
            time.sleep(1.0)
            chk = make_request(job_url, headers=headers, ssl_context=ssl_ctx)
            if chk["status"] == 200 and isinstance(chk["data"], dict):
                st = chk["data"].get("status")
                if st in ("Completed", 2, "2"):
                    return chk["data"]
                if st in ("Failed", 3, "3", "Cancelled", 4, "4"):
                    return None
        return None

    def get_job_url(res):
        hdrs = res.get("headers") or {}
        loc = hdrs.get("Location") or hdrs.get("location")
        if not loc and isinstance(res.get("data"), dict):
            loc = res["data"].get("statusUrl") or (f"/api/v1/projects/{project_id}/generations/{res['data'].get('id')}" if res["data"].get("id") else None)
        return loc

    j1_url = get_job_url(r1)
    j2_url = get_job_url(r2)

    with ThreadPoolExecutor(max_workers=2) as executor:
        pf1 = executor.submit(wait_for_job, j1_url, owner_headers)
        pf2 = executor.submit(wait_for_job, j2_url, designer_headers)
        job1_data = pf1.result()
        job2_data = pf2.result()

    job1_completed = job1_data is not None
    job2_completed = job2_data is not None
    record_step("Concurrent Job 1 Execution", {"status": 200 if job1_completed else 500, "latency_ms": 0, "error": None}, 200)
    record_step("Concurrent Job 2 Execution", {"status": 200 if job2_completed else 500, "latency_ms": 0, "error": None}, 200)

    if not job2_completed or not job2_data.get("outputAssetIds"):
        print("ERROR: Designer job did not generate output assets.")
        return False, audit_log

    asset_id = job2_data["outputAssetIds"][0]

    # 6. Concept Submission & Review Security
    print("\n--- Phase 5: Concept Review & Authorization Boundaries ---")
    concept_res = make_request(
        f"{base_url}/api/v1/projects/{project_id}/characters/{char_id}/concepts",
        method="POST",
        headers=designer_headers,
        data={
            "variantId": None,
            "title": "Kagura Action Stance",
            "description": "Reviewed against character sheets",
            "generationJobId": job2_data["id"],
            "imageAssetId": asset_id
        },
        ssl_context=ssl_ctx
    )
    if not record_step("Designer Creates Concept Version", concept_res, 201):
        return False, audit_log

    concept_id = concept_res["data"]["id"]
    version_id = concept_res["data"]["representativeVersionId"]

    # Designer submits for review
    submit_res = make_request(
        f"{base_url}/api/v1/projects/{project_id}/concepts/{concept_id}/versions/{version_id}/submit",
        method="POST",
        headers=designer_headers,
        ssl_context=ssl_ctx
    )
    record_step("Designer Submits Version for Review", submit_res, 200)

    # Designer attempts self-approval -> MUST FAIL with 403 Forbidden (API-005)
    unauth_approval = make_request(
        f"{base_url}/api/v1/projects/{project_id}/concepts/{concept_id}/versions/{version_id}/review",
        method="POST",
        headers=designer_headers,
        data={"decision": "Approved", "comment": "Designer self approval attempt"},
        ssl_context=ssl_ctx
    )
    record_step("Security Boundary: Designer Denied Approval (403)", unauth_approval, 403)

    # Owner approves -> MUST SUCCEED with 200 OK
    auth_approval = make_request(
        f"{base_url}/api/v1/projects/{project_id}/concepts/{concept_id}/versions/{version_id}/review",
        method="POST",
        headers=owner_headers,
        data={"decision": "Approved", "comment": "Approved by Lead Art Director on PC-01"},
        ssl_context=ssl_ctx
    )
    record_step("Owner Approval Executed (200)", auth_approval, 200)

    # Immutability check: re-approval yields 409 Conflict (UNIT-012)
    re_approval = make_request(
        f"{base_url}/api/v1/projects/{project_id}/concepts/{concept_id}/versions/{version_id}/review",
        method="POST",
        headers=owner_headers,
        data={"decision": "Approved", "comment": "Re-approval attempt"},
        ssl_context=ssl_ctx
    )
    record_step("Immutability Rule: Re-approval Denied (409)", re_approval, 409)

    # Final report
    all_passed = all(entry["passed"] for entry in audit_log)
    print("\n" + "=" * 65)
    print(f" SIMULATION RESULT: {'ALL PASS' if all_passed else 'SOME STEPS FAILED'}")
    print(f" Total Steps:       {len(audit_log)}")
    print(f" Passed Steps:      {sum(1 for e in audit_log if e['passed'])}")
    print("=" * 65)

    return all_passed, audit_log

if __name__ == "__main__":
    url = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5000"
    insecure_flag = "--insecure" in sys.argv or url.startswith("https")
    success, report = run_simulation(url, insecure=insecure_flag)
    
    # Save report
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "dist")
    os.makedirs(out_dir, exist_ok=True)
    report_file = os.path.join(out_dir, "two_client_simulation_report.json")
    with open(report_file, "w", encoding="utf-8") as f:
        json.dump({
            "timestampUtc": datetime.utcnow().isoformat() + "Z",
            "serverUrl": url,
            "overallSuccess": success,
            "steps": report
        }, f, indent=2)
    print(f"Report written to: {report_file}")
    sys.exit(0 if success else 1)
