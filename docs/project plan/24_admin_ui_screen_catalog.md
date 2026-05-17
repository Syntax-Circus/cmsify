# Cmsify Admin — UI Screen Catalog
### Design Handoff Document

---

## Design System Foundations

### Technology
- **Framework:** .NET 10 Blazor Unified Web App
- **CSS:** Bootstrap 5 (loaded via LibMan as SCSS source; compiled via AspNetCore.SassCompiler)
- **Theme:** System-aware light/dark mode via Bootstrap's color mode system (`data-bs-theme="auto"`)
- **Density:** Comfortable (balanced) — standard Bootstrap spacing, not compressed

### Color Modes
- Respect `prefers-color-scheme` by default
- Manual toggle in top nav (sun/moon icon) persists preference to `localStorage`
- All custom SCSS must define values for both `[data-bs-theme="light"]` and `[data-bs-theme="dark"]`

### Typography
- Use Bootstrap's default type scale
- Page titles: `h4` weight
- Section headings: `h6` weight, muted color
- Data labels: `small` + `text-muted`

### Feedback Patterns
- **Success:** Toast notification, top-right, auto-dismiss after 4 seconds. Green accent. Icon: checkmark.
- **Error:** Inline `alert alert-danger` rendered directly below the triggering form or action button. Persists until dismissed or action retried.
- **Loading states:** Spinner inside the triggering button; button disabled during operation. Never full-page spinners.
- **Empty states:** Illustrated placeholder with a heading, one-line description, and a primary CTA button.

### Destructive Action Confirmation
All destructive actions (delete, archive, revoke, rotate secret) open a **Confirm Dialog modal** before proceeding.

**Confirm Dialog structure:**
- Modal title: "Confirm [Action]"
- Body: One sentence describing exactly what will happen and whether it is reversible
- Footer: Two buttons — "Cancel" (secondary, closes modal) and "[Action]" (danger/warning variant, triggers operation)
- The confirm button shows a spinner and disables during the operation

### Breadcrumbs
Present on all inner pages (any page below the top-level list). Format:
`Workspace Name / Section / Page Title`
Rendered as Bootstrap breadcrumb component directly below the top nav bar.

### Modals (used for small CRUD)
- Max width: `modal-lg` (800px) for forms, `modal-sm` for simple confirmations
- Always has a header (title + close X), scrollable body, and sticky footer with actions
- Forms inside modals follow the same inline error banner pattern

---

## Shell / App Chrome

### Responsive Navigation Behavior

**Large screens (≥992px):**
- Fixed left sidebar (240px wide)
- Top nav bar spans the remaining content area
- Sidebar contains: app logo/name, workspace switcher, primary nav links, bottom-anchored user menu

**Medium screens (768px–991px):**
- Left sidebar collapses to icon-only rail (56px wide)
- Hover/focus on an icon shows a tooltip with the nav label
- Top nav bar remains full width of content area

**Small screens (<768px):**
- Left sidebar hidden entirely
- Top nav bar spans full width
- Hamburger menu button (☰) in top nav opens the sidebar as a full-height slide-out overlay from the left
- Overlay has a backdrop; clicking backdrop closes it

---

### Top Nav Bar

**Left side:**
- Hamburger button (small screens only)
- Breadcrumb trail (hidden on xs, visible md+)

**Right side:**
- Light/dark mode toggle (sun/moon icon button)
- Notification bell (future — placeholder only for MVP)
- User avatar/initials dropdown:
  - Displays current user's display name and role badge
  - Menu items: "Account Preferences" → `/account/preferences`, "Change Password" → `/account/change-password`, "Logout"

---

### Left Sidebar

**Top section:**
- App logo + "Cmsify" wordmark (links to `/`)
- Workspace switcher (see below)

**Primary nav links** (icon + label):
- Dashboard (home icon) → `/workspaces/{id}`
- Templates (puzzle piece icon) → `/workspaces/{id}/templates`
- Content (document icon) → `/workspaces/{id}/content`
- Media (image icon) → `/workspaces/{id}/media`
- Settings (gear icon) → `/settings` (expands sub-items inline or navigates to settings landing)

**Settings sub-items** (visible when Settings is active):
- Users (Admin only)
- API Clients (Admin only)
- Webhooks
- Storage (Admin only)
- Audit Log

**Bottom of sidebar:**
- Current user display name + role badge
- Logout link

---

### Workspace Switcher
Located at the top of the sidebar below the logo.

- Displays current workspace name + a chevron-down icon
- Click opens a **dropdown** listing all accessible workspaces
- Each workspace item: name + slug (muted)
- Bottom of dropdown: "Manage Workspaces" link (Admin only) → `/workspaces`
- Selecting a workspace updates `WorkspaceState` and navigates to that workspace's dashboard

---

## Screens

---

## 1. Login
**Route:** `/login`
**Layout:** Full-page centered card (no sidebar, no top nav)

### Components
- App logo + "Cmsify" wordmark centered above card
- Card (`card` with shadow):
  - Title: "Sign in to Cmsify"
  - Email input (type=email, autofocus, required)
  - Password input (type=password, required)
  - "Remember me" checkbox
  - Submit button ("Sign In", full width, primary)
  - Inline error banner below submit (shown on failed login)
  - Divider (shown only if OIDC enabled): "or"
  - "Sign in with {OidcProviderName}" button (outline, full width, provider icon if available)
- Footer text: version number (muted, small)

### Interactions
- Submit → POST `/api/v1/auth/login` → on success store token; if response `mustChangePassword == true`, redirect to `/account/change-password`; otherwise redirect to `returnUrl` or `/workspaces`
- Enter key in either field submits the form
- Button shows spinner + disables during request
- Failed login: inline error banner "Invalid email or password" (rendered from ProblemDetails)
- "Remember me" checked → token persisted to `localStorage`; unchecked → `sessionStorage`

---

## 2. Workspace List
**Route:** `/workspaces`
**Access:** Admin only (others are auto-redirected to their workspace dashboard)
**Layout:** Full content area, no sidebar workspace context

### Components
- Page header: "Workspaces" + "New Workspace" button (primary, top right)
- Search input (filters list client-side by name/slug)
- Workspace cards grid (2-col md, 1-col sm):
  - Each card: workspace name (h6), slug (muted small), content count, template count, active/inactive badge
  - Card footer: "Open" button (primary) + kebab menu (Edit, Deactivate/Activate)
- Empty state: "No workspaces yet" + "Create your first workspace" CTA

### Interactions
- "New Workspace" → opens **New Workspace Modal**
- "Open" → sets workspace context, navigates to `/workspaces/{id}`
- Kebab → Edit → opens **Edit Workspace Modal**
- Kebab → Deactivate → opens **Confirm Dialog** ("This workspace will be hidden from all users. Content and templates are retained.")

### New/Edit Workspace Modal (`modal-lg`)
- Title: "New Workspace" / "Edit Workspace"
- Fields:
  - Name (text, required)
  - Slug (text, required, auto-generated from name, editable; shows uniqueness validation inline)
  - Description (textarea, optional)
- Footer: Cancel + Save

---

## 3. Workspace Dashboard
**Route:** `/workspaces/{id}`
**Layout:** Standard shell with sidebar

### Components
- Page header: workspace name + description (muted)
- Stats row (4 cards): Total Content Items, Published, Templates, Media Assets
- Recent Activity feed (last 10 audit log entries for this workspace): actor name, action description, relative timestamp
- Quick Actions row: "New Content" button, "New Template" button, "Upload Media" button

### Interactions
- Stats cards are clickable → navigate to the relevant section pre-filtered
- Activity feed items are not interactive (read-only) for MVP

---

## 4. Template List
**Route:** `/workspaces/{id}/templates`
**Layout:** Standard shell

### Components
- Page header: "Templates" + "New Template" button (primary, top right)
- Filter bar:
  - Search input (by name)
  - Toggle: "Show system primitives" (off by default)
- Templates table:
  - Columns: Name, Slug, Current Version, Field Count, Last Updated, Actions
  - System primitive rows: visually distinct (muted, lock icon, no edit/delete actions)
  - User template rows: full actions
  - Actions column: "Edit" button + kebab (View Versions, Delete)
- Empty state: "No templates yet — start by creating one"

### Interactions
- "New Template" → opens **New Template Modal**
- "Edit" → navigates to `/workspaces/{id}/templates/{tid}` (Template Builder)
- Kebab → View Versions → navigates to `/workspaces/{id}/templates/{tid}/versions`
- Kebab → Delete → **Confirm Dialog** ("This template and all its versions will be permanently deleted. This cannot be undone if no content references it.")
  - If content references exist: dialog body changes to show error ("Cannot delete — X content items reference this template") with no confirm button, only Close

### New Template Modal (`modal-lg`)
- Title: "New Template"
- Fields:
  - Name (text, required)
  - Slug (text, required, auto-generated from name, editable)
  - Description (textarea, optional)
- Footer: Cancel + "Create Template" (on success navigates directly to Template Builder)

---

## 5. Template Builder
**Route:** `/workspaces/{id}/templates/{tid}`
**Layout:** Standard shell — this page uses a **custom split-panel layout** instead of a standard content area

### Layout
```
┌─────────────────────────────────────────────────────┐
│  Top Bar: Template name | Version badge | Actions   │
├──────────────────┬──────────────────────────────────┤
│  Left Panel      │  Right Panel                     │
│  Structure Tree  │  Selected Item Editor            │
│  (scrollable)    │  (scrollable)                    │
└──────────────────┴──────────────────────────────────┘
```
- Left panel: 320px fixed, right panel: remaining width
- On mobile (<768px): panels stack vertically (tree on top, editor below)

### Top Bar Components
- Template name (editable inline — click to edit, blur to save)
- Version badge: "v{n}" + status chip ("Draft" warning / "Published" success)
- Button group (right-aligned):
  - "Save Draft" (secondary) — saves current unpublished changes
  - "Publish Version" (primary) — opens **Publish Version Modal**
  - "New Version" (outline) — opens **Confirm Dialog** ("A new draft version will be created as a copy of the current published version.")
  - "Version History" link → navigates to version history page

### Left Panel — Structure Tree
- "Add Section" button at top
- Section items (collapsible):
  - Section header: drag handle (⠿) + section name + edit (pencil) + delete (trash) icons
  - Indented field list within each section:
    - Field item: drag handle + field label + type chip (e.g. "Text", "Media", "BlogPost") + required indicator (*) + edit icon
    - "Add Field" button at bottom of each section's field list
- "Add Field (no section)" button below all sections (for root-level fields)
- Clicking any section or field item → loads its editor in the Right Panel, highlights the selected item

**Published version state:** All drag handles, add buttons, and edit/delete icons are hidden. Read-only indicators shown. Banner at top of left panel: "This version is published and cannot be modified. Create a new version to make changes."

### Right Panel — Section Editor (when section selected)
- Title: "Edit Section"
- Fields:
  - Name (text, required)
  - Description (textarea, optional)
  - Is Collapsible (toggle switch)
- Auto-saves on blur (no explicit save button for section metadata)

### Right Panel — Field Editor (when field selected)
- Title: "Edit Field" / "New Field"
- Fields:
  - Label (text, required)
  - Key (text, required, auto-generated from label in slug format, editable; unique within version)
  - Help Text (text, optional — shown as hint beneath the field in the content editor)
  - Required (toggle switch)
  - Min Occurrences (number input, default 0; disabled and set to 1 when Required=true)
  - Max Occurrences (number input, placeholder "∞" for unbounded; min 1)
  - Composition Mode (radio buttons: "Inline" / "Reference" — only shown when type is a Template)
  - **Type Picker** (see below)
  - **Allowed Types** (multi-select, shown only when IsOpen=false and type supports multiple — see below)
  - Is Open (toggle — "Accept any type"; when enabled, Allowed Types hidden)
- Footer: "Remove Field" (danger, left-aligned) + "Apply" (primary, right-aligned)
- Cycle detection warning: if the selected type would create a circular reference, show an inline `alert-warning` beneath the Type Picker: "Adding this type would create a circular reference: A → B → A. Choose a different type."

### Type Picker Component
- Displayed as a searchable grouped select or button-group tabs:
  - Group 1: "Primitives" — Text, RichText, Markdown, Boolean, PickList, Media, File, Link, Quote, Separator (shown as pill buttons or chips)
  - Group 2: "Templates" — all user-defined templates in the workspace (excluding self); shown as a searchable list if >6 exist
- Selected type shown as a highlighted chip above the picker
- Changing type resets Allowed Types

### Allowed Types Multi-Select
- Shown when: field type is a Template or IsOpen=false
- Checkboxes listing all primitives and user templates
- "Select all" / "Clear" shortcuts
- Disabled (and hidden) when IsOpen=true

### Publish Version Modal (`modal-lg`)
- Title: "Publish Version v{n}"
- Body: "Once published, this version cannot be modified. Existing content pinned to previous versions is unaffected."
- Release notes textarea (optional)
- Footer: Cancel + "Publish" (primary)

---

## 6. Version History
**Route:** `/workspaces/{id}/templates/{tid}/versions`
**Layout:** Standard shell

### Components
- Breadcrumb: Workspace / Templates / {Template Name} / Version History
- Page header: "{Template Name} — Version History"
- Versions table:
  - Columns: Version, Status (Draft/Published), Field Count, Published At, Notes, Actions
  - Actions: "View" (navigates to read-only builder for that version), "Set as Current" (for published versions — Admin only)
- Current version row: highlighted with "Current" badge

### Interactions
- "View" → navigates to Template Builder in read-only mode for that version number
- No editing from this page

---

## 7. Content List
**Route:** `/workspaces/{id}/content`
**Layout:** Standard shell

### Components
- Page header: "Content" + "New Content" button (primary, top right)
- Filter bar (collapsible on mobile):
  - Status dropdown (All, Draft, Review, Approved, Published, Archived)
  - Template picker dropdown (searchable, lists all templates in workspace)
  - Locale input (text filter)
  - Tags input (tokenized, filter by tag)
  - Date range picker (Published At)
  - Search input (by slug)
  - "Clear Filters" link (shown only when any filter is active)
- Content table:
  - Columns: Title (slug or first Text field value), Template, Status (badge), Locale, Tags (chips, max 3 shown + "+N more"), Updated At, Actions
  - Actions column: "Edit" button + kebab (lifecycle transitions available for current status, Delete)
  - Status badge colors: Draft (secondary), Review (warning), Approved (info), Published (success), Archived (muted)
- Pagination controls (bottom): page size selector (10/20/50) + page navigation
- Empty state (no content): "No content yet" + "Create your first content item" CTA
- Empty state (filtered, no results): "No results match your filters" + "Clear Filters" link

### Interactions
- "New Content" → opens **New Content Modal** (template picker)
- "Edit" → navigates to `/workspaces/{id}/content/{cid}`
- Kebab lifecycle actions (available options depend on current status and actor role):
  - Draft: Submit for Review
  - Review: Approve, Send Back to Draft
  - Approved: Publish Now, Schedule Publish, Archive
  - Published: Archive
  - Archived: Restore to Draft
  - All statuses (Editor+): Delete
- Any lifecycle action from the list → **Confirm Dialog** → executes → toast success / inline error on row

### New Content Modal (`modal-lg`)
- Title: "New Content Item"
- Fields:
  - Template picker (searchable dropdown, required — lists all non-system templates)
  - Locale Code (text input, optional, placeholder "e.g. en, fr-CA")
  - Slug (text, optional, placeholder "auto-generated if blank")
- Footer: Cancel + "Create & Edit" (navigates to Content Editor on success)

---

## 8. Content Editor
**Route:** `/workspaces/{id}/content/{cid}`
**Layout:** Standard shell — custom two-column layout

```
┌─────────────────────────────────────────┬──────────────────┐
│  Main Content Area                      │  Sidebar         │
│  (dynamic field form)                   │  (lifecycle +    │
│                                         │   metadata)      │
└─────────────────────────────────────────┴──────────────────┘
```
- Main area: fluid width. Sidebar: 280px fixed right, scrolls independently.
- On mobile: sidebar moves below the form.

### Main Area — Field Form
- Template name + version badge at top (read-only, muted)
- If template has sections: fields are grouped under collapsible `card` components with section name as card header
- If no sections: fields rendered as a flat list with `mb-4` spacing between them
- Each field renders its appropriate input component (see Field Components below)
- Multi-occurrence fields: rendered as a vertical list with drag-to-reorder handles, "Add another" button below, remove (×) button on each item
- Required fields: asterisk (*) in label
- Help text: rendered as `form-text` beneath the input

### Field Components

| Field Type | Component |
|------------|-----------|
| Text | `<input type="text">` single line |
| RichText | Rich text editor toolbar + contenteditable area (Quill or TipTap via JS interop); toolbar: bold, italic, underline, strikethrough, headings, lists, blockquote, link, clear formatting |
| Markdown | Split view: left textarea, right rendered preview (toggle between edit-only / split / preview modes) |
| Boolean | Toggle switch with "Yes" / "No" label |
| PickList | `<select>` dropdown (single) or checkbox group (multi); options defined in field config |
| Media | Thumbnail preview (if selected) + "Choose Media" button → opens Media Picker Modal; "Remove" link if value set |
| File | Filename display (if selected) + "Choose File" button → opens Media Picker Modal (filtered to non-image); "Remove" link |
| Link | Two inputs: URL (`type="url"`) + Link Text (`type="text"`); optional "Open in new tab" checkbox |
| Quote | Textarea for quote text + text input for attribution/source |
| Separator | Non-interactive visual divider with label "— Separator —" (muted); no input |
| Template (Inline) | Embedded sub-form rendered inside a `card` with a muted header showing the template name; recursive (can nest further sub-forms) |
| Template (Reference) | Search input with autocomplete → shows matching content items of the allowed type(s); selected item shown as a card with name, status badge, "Change" and "Remove" links |

### Sidebar Components

**Status & Lifecycle**
- Current status badge (large)
- Available transition buttons (based on status + role):
  - "Submit for Review" (secondary)
  - "Approve" (info)
  - "Send Back to Draft" (warning, with required reason text input inline)
  - "Publish Now" (success)
  - "Archive" (secondary)
  - "Restore to Draft" (secondary)
- Each transition button → **Confirm Dialog** → executes → toast + status badge updates

**Scheduled Publish** (shown when status = Approved)
- Date + time picker: "Publish At"
- "Schedule" button (sets PublishAt, saves)
- If already scheduled: shows "Scheduled for {datetime}" + "Cancel Schedule" link

**Metadata**
- Slug (text input, optional; shows auto-generate hint)
- Locale Code (text input, optional)
- Tags (tokenized tag input — type and press Enter/comma to add; existing workspace tags shown as autocomplete suggestions)
- Translation Group:
  - If not in a group: "Link Translation" button → opens **Link Translation Modal**
  - If in a group: "In translation group" + "View all translations" link + "Unlink" option

**Save Controls**
- "Save Draft" button (primary, full width) — always visible
- Auto-save indicator: "Saved {relative time}" or "Unsaved changes" (updates every 30s if changes detected)
- "Delete" link (danger, bottom of sidebar) → **Confirm Dialog**

### Link Translation Modal (`modal-lg`)
- Title: "Link Translation"
- Body: Search input to find an existing content item of the same template type
- Results list: content item title, locale badge, status badge
- "Link Selected" button
- Note: "Linking creates a shared Translation Group ID between these items"

---

## 9. Media Library
**Route:** `/workspaces/{id}/media`
**Layout:** Standard shell

### Components
- Page header: "Media Library" + "Upload" button (primary, top right)
- Filter bar:
  - Type filter tabs: All | Images | Video | Audio | Documents
  - Search input (by filename)
- View toggle: Grid (default) / List (icon buttons, top right of content area)
- **Grid view:** responsive grid (4-col lg, 3-col md, 2-col sm)
  - Each cell: thumbnail (image preview or file-type icon for non-images), filename (truncated), file size (muted small)
  - Hover state: overlay with "Select" checkbox and "Details" button
- **List view:** table with columns: Thumbnail (small), Filename, Type, Size, Uploaded By, Uploaded At, Actions
  - Actions: "Details" + "Delete"
- Pagination controls (bottom)
- Empty state: drag-and-drop upload zone with dashed border + "Drop files here or click Upload"

### Interactions
- "Upload" button → opens **Upload Modal**
- Click asset in grid → opens **Asset Detail Panel** (slide-in from right, not a modal)
- Delete (from list view or detail panel) → **Confirm Dialog** ("This file will be permanently deleted from storage." / warning if referenced by published content)

### Upload Modal (`modal-lg`)
- Title: "Upload Media"
- Drag-and-drop zone (large, dashed border) with "or browse files" link
- Supports multi-file selection
- File list below zone: each file shows name, size, upload progress bar, success checkmark or error message
- Upload begins immediately on file selection (not on a submit button)
- Modal footer: "Done" button (closes modal; success toast shows count uploaded)

### Asset Detail Panel (slide-in overlay, not modal)
- Header: filename + close (×) button
- Large preview (image rendered full width; other types show large file-type icon)
- Metadata section: file type, size, dimensions (images), uploaded by, uploaded at
- Alt Text input (editable, save on blur)
- "Copy URL" button (copies the API retrieval URL to clipboard)
- "Delete" button (danger) → **Confirm Dialog**

---

## 10. Settings — Users
**Route:** `/settings/users`
**Access:** Admin only
**Layout:** Standard shell with settings sub-nav

### Components
- Page header: "Users" + "Create User" button (primary, top right)
- Users table:
  - Columns: Avatar/Initials, Display Name, Email, Role (badge), Last Login (relative), Status (Active/Inactive badge), Actions
  - Actions: "Edit Role" (opens modal) + kebab (Reset Password / Deactivate / Activate)
  - Current user row: "You" badge; Deactivate action disabled
- Empty state: not applicable (always at least one admin)

### Interactions
- "Create User" → opens **Create User Modal**
- "Edit Role" → opens **Edit Role Modal**
- Kebab → Reset Password → opens **Reset Password Modal** (admin sets a new temporary password; user is flagged `MustChangePassword`)
- Kebab → Deactivate → **Confirm Dialog** ("This user will no longer be able to log in. Their content and audit history is retained.")
- Kebab → Activate → executes immediately (no confirm; reversible)

### Create User Modal (`modal-lg`)
- Title: "Create User"
- Fields:
  - Display Name (text, required)
  - Email (email, required)
  - Role (radio group: Reader, Editor, TemplateAdmin, Admin; each with a one-line description)
  - Temporary Password (text, required; show/hide toggle; "Generate" button alongside)
- Note: "The user will be required to change this password on first sign-in. There is no email delivery in MVP — communicate the temporary password to the user securely (Slack, in person, password manager, etc.)."
- Footer: Cancel + "Create User"
- On success: **Temporary Password Reveal Modal** displays the credentials one final time with a copy button before dismissal.

### Reset Password Modal (`modal-sm`)
- Title: "Reset Password — {Display Name}"
- Field: New Temporary Password (text, required; show/hide; "Generate" button)
- Note: "User will be forced to change this on next sign-in."
- Footer: Cancel + "Reset"
- On success: Temporary Password Reveal pattern (copy once).

### Edit Role Modal (`modal-sm`)
- Title: "Edit Role — {Display Name}"
- Role radio group (same as above)
- Footer: Cancel + "Save"

---

## 10b. Account — Preferences
**Route:** `/account/preferences`
**Access:** Any signed-in user
**Layout:** Standard shell

### Components
- Page header: "Account Preferences"
- Section: **Identity** (read-only) — Display Name, Email, Role
- Section: **Localization**
  - Time Zone (searchable select — IANA TZ database; defaults to browser-detected zone on first load; persisted to `User.TimeZoneId`)
  - Note: "All timestamps in the admin UI render in your selected time zone."
- Section: **Appearance**
  - Theme (radio: Light / Dark / System)
- Section: **Security**
  - "Change Password" link button → `/account/change-password`

---

## 10c. Account — Change Password
**Route:** `/account/change-password`
**Access:** Any signed-in user. **Forced** by router guard when `AuthState.MustChangePassword == true` — nav menu is hidden and other routes redirect back here.

### Components
- Page header: "Change Password"
- Banner (only when forced): "Your administrator set a temporary password. Please choose a new password to continue."
- Fields: Current Password, New Password, Confirm New Password (all show/hide toggles)
- Footer: "Change Password" primary button
- Inline ProblemDetails error rendering on failure (e.g. wrong current password)
- On success: clears `MustChangePassword`, toasts "Password changed", redirects to `/workspaces` (or `returnUrl`)

---

## 11. Settings — API Clients
**Route:** `/settings/api-clients`
**Access:** Admin only
**Layout:** Standard shell with settings sub-nav

### Components
- Page header: "API Clients" + "New API Client" button (primary, top right)
- API clients table:
  - Columns: Name, Role, Workspace Scope, Created, Last Used, Expires, Status, Actions
  - Workspace Scope: workspace name or "All Workspaces"
  - Actions: "Rotate Token" + kebab (Revoke)
- Empty state: "No API clients yet" + explainer: "API clients allow external applications to access Cmsify programmatically."

### Interactions
- "New API Client" → opens **Create API Client Modal**
- After create: opens **Token Reveal Modal** (one-time display)
- "Rotate Token" → **Confirm Dialog** ("A new token will be issued. The current token will stop working immediately.") → on confirm: opens **Token Reveal Modal**
- Kebab → Revoke → **Confirm Dialog** ("This client will immediately lose API access.")

### Create API Client Modal (`modal-lg`)
- Title: "New API Client"
- Fields:
  - Name (text, required)
  - Description (textarea, optional)
  - Role (radio group: Reader, Editor, TemplateAdmin, Admin)
  - Workspace Scope (select: "All Workspaces" or pick a specific workspace)
  - Expires At (date picker, optional; label: "Leave blank for no expiry")
- Footer: Cancel + "Create Client"

### Token Reveal Modal (`modal-lg`)
- Title: "Your API Token"
- **Warning banner** (yellow/warning): "This token will only be displayed once. Copy it now and store it securely — it cannot be retrieved again."
- Token display: monospace text in a read-only input + "Copy" button (copies to clipboard; button changes to "Copied ✓" for 2s)
- Footer: "I've copied my token" button (primary, closes modal; disabled until Copy has been clicked at least once)

---

## 12. Settings — Webhooks
**Route:** `/settings/webhooks`
**Access:** Editor+
**Layout:** Standard shell with settings sub-nav

### Components
- Page header: "Webhooks" + "New Webhook" button (primary, top right)
- Webhooks table:
  - Columns: Name, URL (truncated), Events (chips, max 3 + "+N"), Status (Active/Inactive), Success Rate (last 50 deliveries, shown as % badge), Actions
  - Actions: "View Deliveries" + kebab (Edit, Rotate Secret, Deactivate/Activate, Delete)
- Empty state: "No webhooks configured" + brief explainer

### Interactions
- "New Webhook" → opens **Webhook Form Modal**
- "View Deliveries" → navigates to `/settings/webhooks/{id}/deliveries`
- Kebab → Edit → opens **Webhook Form Modal** (pre-populated)
- Kebab → Rotate Secret → **Confirm Dialog** → on confirm opens **Secret Reveal Modal**
- Kebab → Deactivate/Activate → executes immediately (reversible; no confirm)
- Kebab → Delete → **Confirm Dialog** ("This webhook and all its delivery history will be permanently deleted.")

### Webhook Form Modal (`modal-lg`)
- Title: "New Webhook" / "Edit Webhook"
- Fields:
  - Name (text, required)
  - URL (url input, required)
  - Secret (text input, required on create; on edit shows "••••••••" + "Rotate Secret" link instead of input)
  - Active (toggle switch)
  - Events (checkbox group, organized by category):
    - Content: content.created, content.updated, content.status_changed, content.published, content.archived, content.deleted
    - Templates: template.version_published
    - Workspace: workspace.updated
  - "Select All" / "Clear All" shortcuts per category
- Footer: Cancel + Save

### Secret Reveal Modal
- Same pattern as Token Reveal Modal (warning banner, monospace display, copy button, "I've copied it" close)

### Webhook Deliveries
**Route:** `/settings/webhooks/{id}/deliveries`
**Layout:** Standard shell

- Breadcrumb: Settings / Webhooks / {Webhook Name} / Deliveries
- Page header: "{Webhook Name} — Delivery Log"
- Filter bar: Status (All / Delivered / Failed / Pending), Event Type dropdown, Date range
- Deliveries table:
  - Columns: Event Type, Status (badge: Delivered/Failed/Pending), Attempts, Last Attempt, Response Code, Actions
  - Actions: "Retry" (for failed/pending items)
- Expandable row: click any row to expand and show full payload JSON (syntax-highlighted, read-only)
- "Retry" → executes immediately → toast success / inline row error

---

## 13. Settings — Storage
**Route:** `/settings/storage`
**Access:** Admin only
**Layout:** Standard shell with settings sub-nav

### Components
- Page header: "Storage Configuration"
- Info banner (blue): "Storage provider is configured via environment variables or appsettings. Changes require a restart."
- Current configuration card:
  - Provider type (e.g. "Local Filesystem" or "S3-Compatible")
  - Provider-specific details: Base Path (local) or Bucket Name + Region (S3)
  - "Test Connection" button → POST `/api/v1/settings/storage/test` → inline success/error result beneath button
- Documentation link: "How to configure storage providers" (links to docs)

### Interactions
- "Test Connection" → button shows spinner → displays inline result: green "Connection successful" or red error message with detail

---

## 14. Settings — Audit Log
**Route:** `/settings/audit`
**Access:** TemplateAdmin+
**Layout:** Standard shell with settings sub-nav

### Components
- Page header: "Audit Log"
- Filter bar:
  - Entity Type dropdown (All, ContentItem, Template, User, ApiClient, Workspace, WebhookEndpoint)
  - Action dropdown (All, Created, Updated, Deleted, StatusChanged)
  - Actor input (search by user display name or API client name)
  - Date range picker (After / Before)
  - "Clear Filters" link
- Audit table:
  - Columns: Timestamp, Entity Type, Action (badge), Entity ID (truncated UUID, copy on click), Actor, Workspace
  - Expandable row: click to expand and show Change Delta as formatted key→value diff (before/after values, color-coded: red for removed/old, green for added/new)
- Pagination (50 per page default)
- Empty state: "No audit entries match your filters"

---

## 15. Onboarding — Template Presets (Post-MVP)
**Route:** `/onboarding/templates`
**Layout:** Full-page (no sidebar); shown only on first login after workspace creation

### Components
- Centered layout with max-width container
- Heading: "Get started with template presets"
- Subheading: "Choose from our official packs to hit the ground running. You can always add more later."
- Preset pack cards (2-col md, 1-col sm):
  - Each card: pack name, description, list of included templates (as chips), "Select" toggle (card highlights when selected)
- "Install Selected" button (primary, bottom) — disabled until at least one pack selected; shows count: "Install 2 packs"
- "Skip for now" link (muted, right of button)
- Progress: spinner overlay during install, then redirect to Templates list with success toast

---

## 16. Settings — Packages (Post-MVP)
**Route:** `/settings/packages`
**Access:** TemplateAdmin+
**Layout:** Standard shell with settings sub-nav

### Components
- Page header: "Template Packages" + "Import Package" button (primary, top right)
- Installed packages table:
  - Columns: Package (namespace/id), Name, Version, Templates (count), Installed At, Actions
  - Actions: "View Templates" (navigates to Templates list filtered by package), "Export" (downloads `.ctp`)
- Empty state: "No packages installed" + "Import a package" CTA

### Import Package Modal (`modal-lg`)
- Title: "Import Template Package"
- Two tabs:
  - "Upload File": drag-and-drop or browse for `.ctp` file; on file select shows package name/version/template count parsed from manifest; "Import" button
  - "Import from URL": URL input + "Fetch & Preview" button; shows same preview card on success; "Import" button
- Preview card (shown after file/URL loaded):
  - Package name, namespace/id, version
  - Description
  - Included templates list (chips)
  - Conflict warning (if a version of this package already installed): "Version {x} is already installed. Importing will create new template versions."
- Footer: Cancel + "Import" (disabled until preview loaded)

---

## Shared Component Inventory

| Component | Usage |
|-----------|-------|
| `<Toast>` | Global success notifications (top-right stack, auto-dismiss 4s) |
| `<ConfirmDialog>` | All destructive actions |
| `<Breadcrumb>` | All inner pages |
| `<StatusBadge>` | Content item status, user/client active state |
| `<Pagination>` | All list pages |
| `<EmptyState>` | All list pages (no data, no results) |
| `<LoadingButton>` | All async action buttons (spinner + disabled during operation) |
| `<TagInput>` | Content editor sidebar, content list filter |
| `<SearchableSelect>` | Template picker, workspace picker, actor filter |
| `<MediaPickerModal>` | Media and File field components in content editor |
| `<TokenReveal>` | API client create/rotate, webhook secret rotate |
| `<RichTextEditor>` | RichText field in content editor |
| `<MarkdownEditor>` | Markdown field in content editor |
| `<DateTimePicker>` | Scheduled publish, audit log filter |
| `<DragList>` | Template builder field reorder, multi-occurrence field reorder |
| `<JsonViewer>` | Audit log change delta, webhook delivery payload |
| `<SplitPanel>` | Template Builder, Content Editor (two-column layouts) |
| `<SlidePanel>` | Media asset detail |
| `<WorkspaceSwitcher>` | Sidebar |
| `<NavShell>` | App chrome (sidebar + top nav + responsive hamburger) |

---

## Modal Inventory

| Modal | Trigger | Size |
|-------|---------|------|
| New/Edit Workspace | Workspace List | `modal-lg` |
| New Template | Template List | `modal-lg` |
| Publish Version | Template Builder | `modal-lg` |
| New Content | Content List | `modal-lg` |
| Link Translation | Content Editor sidebar | `modal-lg` |
| Upload Media | Media Library | `modal-lg` |
| Create User | Settings — Users | `modal-lg` |
| Reset Password | Settings — Users | `modal-sm` |
| Temporary Password Reveal | Settings — Users (create/reset) | `modal-lg` |
| Edit Role | Settings — Users | `modal-sm` |
| Create API Client | Settings — API Clients | `modal-lg` |
| Token Reveal | API Client create/rotate | `modal-lg` |
| Webhook Form | Settings — Webhooks | `modal-lg` |
| Secret Reveal | Webhook rotate | `modal-lg` |
| Import Package (Post-MVP) | Settings — Packages | `modal-lg` |
| Confirm Dialog | All destructive actions | `modal-sm` |

---

## Page Inventory Summary

| # | Route | Type | Access |
|---|-------|------|--------|
| 1 | `/login` | Full-page | Public |
| 1b | `/account/change-password` | Full-page (when forced) / standard | Signed-in (forced on first login) |
| 1c | `/account/preferences` | Standard | Signed-in |
| 2 | `/workspaces` | List page | Admin |
| 3 | `/workspaces/{id}` | Dashboard | All |
| 4 | `/workspaces/{id}/templates` | List page | All |
| 5 | `/workspaces/{id}/templates/{tid}` | Builder (split panel) | TemplateAdmin+ |
| 6 | `/workspaces/{id}/templates/{tid}/versions` | List page | All |
| 7 | `/workspaces/{id}/content` | List page | All |
| 8 | `/workspaces/{id}/content/{cid}` | Editor (split panel) | Editor+ |
| 9 | `/workspaces/{id}/media` | Gallery page | Editor+ |
| 10 | `/settings/users` | List page | Admin |
| 11 | `/settings/api-clients` | List page | Admin |
| 12 | `/settings/webhooks` | List page | Editor+ |
| 12a | `/settings/webhooks/{id}/deliveries` | List page | Editor+ |
| 13 | `/settings/storage` | Config page | Admin |
| 14 | `/settings/audit` | Log viewer | TemplateAdmin+ |
| 15 | `/onboarding/templates` | Full-page wizard | Admin (Post-MVP) |
| 16 | `/settings/packages` | List page | TemplateAdmin+ (Post-MVP) |
