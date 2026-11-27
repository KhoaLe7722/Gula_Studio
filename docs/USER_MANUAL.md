# CharacterConceptAI — User Manual & Operations Guide

Welcome to **CharacterConceptAI**, a studio-grade generative AI character development and concept art orchestration platform designed for anime series, gaming, and visual storytelling productions.

---

## 1. Getting Started

### 1.1 Client Installation
The desktop application is completely self-contained. It requires **no .NET SDK or developer tooling** installed on your workstation.
1. Copy the provided application folder (`dist/desktop/`) to your PC (e.g. `C:\Users\<User>\CharacterConceptAI\`).
2. Double-click `CharacterConceptAI.Desktop.exe` to start.

### 1.2 Connecting to the Studio Server
By default, the client looks for the server at `https://localhost:5001`. To connect to a studio server on the LAN:
- **Option A (Configuration File):** Open `client-config.json` in the application directory and update the `ServerUrl`:
  ```json
  {
    "ServerUrl": "https://192.168.0.108:5001"
  }
  ```
- **Option B (Command Line Flag):** Launch from terminal or shortcut target:
  ```cmd
  CharacterConceptAI.Desktop.exe --server https://192.168.0.108:5001
  ```
- **Option C (Environment Variable):** Set `CHARACTER_CONCEPT_AI_SERVER_URL=https://192.168.0.108:5001`.

---

## 2. Authentication & User Roles

### Role Permissions Matrix

| Capability | Administrator | Project Owner | Designer | Viewer |
| :--- | :---: | :---: | :---: | :---: |
| Server Account Provisioning | Yes | No | No | No |
| Create / Archive Projects | Yes | Yes (Created projects) | No | No |
| Invite / Manage Project Members | Yes | Yes | No | No |
| Create / Edit Characters & Variants | Yes | Yes | Yes | No |
| Trigger AI Concept Generation Jobs | Yes | Yes | Yes | No |
| Submit Concept Versions for Review | Yes | Yes | Yes | No |
| **Approve / Reject Concept Versions** | Yes | **Yes (Lead)** | **No (Denied 403)** | **No** |
| View Gallery, Scripts & Scenes | Yes | Yes | Yes | Yes |

---

## 3. Project & Team Collaboration

1. **Creating a Project:**
   - Click **"Dự án mới"** in the sidebar.
   - Enter Title, Production Type (Game, Anime Series, Light Novel), and Synopsis.
   - You automatically become the **Owner** of the created project.
2. **Inviting Team Members:**
   - Navigate to **"Thành viên dự án"**.
   - Enter your teammate's registered email address.
   - Select their role (`Designer` or `Viewer`) and click **"Thêm thành viên"**.

---

## 4. Character Creation & Attribute Tagging

1. **Character Definition:**
   - In your active project, navigate to **"Nhân vật"** -> **"Thêm nhân vật"**.
   - Specify Name, Archetype Role (Protagonist, Antagonist, Supporting), and Backstory.
2. **Visual Variants & Outfits:**
   - Characters support multiple visual variants (e.g. *Standard Uniform*, *Battle Armor*, *Casual Cloak*).
   - Configure attributes per variant: Hair color/style, eye color, wardrobe details, signature props.
3. **Character Relationship Network:**
   - Define character-to-character dynamics (e.g. *Allies*, *Rivals*, *Master-Apprentice*).
   - Relationships are automatically referenced when synthesizing multi-character scenes.

---

## 5. AI Prompt Studio & Concept Generation

### 5.1 Prompt Modes
- **Hybrid Mode (Recommended):** Automatically injects character tags, variant attributes, and style directives around your custom scenario prompt.
- **Raw Mode:** Complete manual control over the positive prompt string sent to the diffusion model.
- **Template Mode:** Standardized studio prompt recipes for consistent character turnaround sheets.

### 5.2 Reference Image-to-Image (Img2Img)
To iterate upon an existing approved concept art or sketch:
1. Select **"Use Reference Image"** in the generation panel.
2. Select an existing approved asset from the project gallery or upload a concept sketch.
3. Adjust **Denoise Strength** (0.2 for subtle variations, 0.65 for creative reimagining).
4. Click **"Tạo ảnh"** to submit the job to the GPU queue.

### 5.3 Queue Monitoring
- Track real-time progress: Pending -> Processing -> Completed.
- Job entries record execution time, seed, prompt metadata, and GPU hardware status.

---

## 6. Concept Versioning & Approval Workflow

1. **Submitting a Version:**
   - Once a generation job completes, open the output asset in the preview inspector.
   - Click **"Tạo Concept Version"**. Add descriptive version notes and click **"Gửi duyệt"** (Submit for Review).
2. **Owner Review & Approval:**
   - The Project Owner reviews the concept in the **"Phê duyệt"** panel.
   - Owner can click **"Phê duyệt"** (Approve) or **"Từ chối"** (Reject) with mandatory audit feedback comments.
   - *Security Rule:* If a Designer attempts to self-approve a version, the server strictly blocks the action with **HTTP 403 Forbidden**.
3. **Immutability of Approved Assets:**
   - Once marked `Approved`, the concept bytes and metadata are sealed permanently to guarantee production pipeline consistency. Any subsequent re-approval attempt is rejected with **HTTP 409 Conflict**.

---

## 7. Scripts & Scenes Orchestration

1. **Creating Scripts & Acts:**
   - Author episodic scripts, screenplays, and narrative outlines.
2. **Scene Breakdowns:**
   - Break down scripts into distinct numbered scenes.
   - Set scene metadata: Setting location, Time of day (Dawn, Noon, Sunset, Night), Weather, and Emotional mood.
3. **Character Casting & Dialogues:**
   - Link project characters to specific scenes.
   - Specify costume variants and emotional expressions for accurate storyboarding.

---

## 8. Common Troubleshooting

| Symptom | Cause | Solution |
| :--- | :--- | :--- |
| **"Không thể kết nối đến máy chủ"** | Server service stopped or firewall blocking port | Verify server IP, ensure `CharacterConceptAIServer` service is running, verify firewall allowlist. |
| **"Lỗi bảo mật chứng chỉ TLS"** | Client workstation does not trust server root CA | Run `dist/certs/trust-cert-on-client.ps1` with Administrator privileges on the client PC. |
| **"403 Forbidden khi phê duyệt"** | Current user account does not have Owner role | Only the project Owner / Art Lead can approve concept versions. |
| **"Generation Job thất bại (CUDA OOM)"** | GPU VRAM exhausted on server host | Lower generation resolution to 512x512, close external VRAM-heavy applications on server PC. |
