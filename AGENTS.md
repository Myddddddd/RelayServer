# AGENTS.md — Slagent Agent Rules

This file defines mandatory behavior for the Copilot Agent when working with Slagent tools.
All rules below are REQUIRED and must be followed strictly.

---

## Available Tools

- Slagent: Get All Message  
  Fetch all messages and mark them as read by agent.

- Slagent: Get All Unread Message  
  Fetch only unread user messages and mark them as read.

- Slagent: Wait For User Message  
  Block until the user sends a new message.

- Slagent: Send Message  
  Send a message from agent to user.

---

## Mandatory Workflow Rules

### 1. Always check unread messages first

Before starting ANY meaningful action (analysis, research, implementation, planning):

- The agent MUST call **Slagent: Get All Unread Message**
- If unread user messages exist:
  - The agent MUST process them first
  - The agent MUST NOT continue previous tasks until addressed
- When starting a new task, the agent SHOULD consult design docs in **Docs/Design** to quickly understand how the system works.

---

### 2. User confirmation is mandatory

If any information is missing, ambiguous, or requires a decision:

- The agent MUST ask the user via **Slagent: Send Message**
- After asking, the agent MUST call **Slagent: Wait For User Message**
- The agent MUST NOT assume or auto-complete without user input

---

### 3. Task completion confirmation is required

Before finishing ANY major task or response:

- The agent MUST send a confirmation question, such as:
  - “Bạn có hài lòng với kết quả này không?”
  - “Bạn có muốn tôi tiếp tục hay chỉnh sửa gì không?”
- After sending, the agent MUST call **Slagent: Wait For User Message**
- The agent may stop ONLY after the user confirms satisfaction or asks to stop

---

### 4. Session handling

- If a tool is called without a sessionId, the default session created by the extension is acceptable
- All fetched user messages are considered read after tool execution

---

## Failure Conditions (STRICT)

The agent is considered incorrect if it:

- Continues work without checking unread messages
- Assumes user intent without explicit confirmation
- Finishes a task without asking for user approval
- Skips waiting after asking a question

---

## Expected Default Flow

1. Call Get All Unread Message
2. Process unread messages
3. Ask user if clarification is needed
4. Wait for user response
5. Perform task
6. Ask for satisfaction confirmation
7. Wait for user response
8. Stop only after confirmation
