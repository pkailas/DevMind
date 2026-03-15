# DevMind User Guide

## Overview

DevMind is a Visual Studio extension that provides local LLM (Large Language Model) assistance directly inside your IDE. It allows you to generate code, edit existing files, run shell commands, and get AI-powered help without leaving Visual Studio.

**Key Features:**
- Local LLM integration via LM Studio (no cloud dependencies)
- Direct file creation and editing within VS
- Shell command execution with output capture
- Context-aware responses based on your active editor selection
- Dark-themed CC-style interface for a clean coding experience

---

## Getting Started

### Installation

1. Install the DevMind VSIX extension in Visual Studio 2022+
2. Ensure LM Studio is running locally at `http://localhost:1234` (or configure your endpoint in Options)
3. Open Visual Studio and load a solution or project

### Accessing DevMind

Go to **View → Other Windows → DevMind** to open the tool window, or use the keyboard shortcut if configured.

---

## Interface Layout

The DevMind window consists of:

1. **Input TextBox (Top)** - Type your requests here
2. **Toolbar Row** - Contains four buttons: Ask, Run, Stop, Clear
3. **OutputBox (Bottom)** - Dark-themed RichTextBox displaying responses and output

### Colors in OutputBox

- **White/Normal**: Standard LLM responses
- **Blue/Input**: Your input messages
- **Green/Success**: Successful operations
- **Red/Error**: Errors and warnings
- **Gray/Dim**: System messages and metadata

---

## Core Functions

### Ask Button (or Enter Key)

Sends your message to the local LLM for a response. The LLM can:
- Answer questions about code, architecture, or best practices
- Generate new files using the `FILE:` directive
- Edit existing files using the `PATCH` directive
- Request file context using the `READ` directive
- Run shell commands using the `SHELL:` directive

**Example:**
```
Create a utility class for string validation with null checks and trimming
```

### Run Button (or Ctrl+Enter)

Executes shell commands in PowerShell. Use this for:
- Running build commands (`dotnet build`)
- Git operations (`git commit -am "msg"; git push`)
- Any terminal command you would normally run

**Example:**
```
dotnet build; dotnet test
```

Note: In PowerShell, use `;` to chain commands (not `&&`).

### Stop Button

Stops the current LLM response or file generation in progress. Use this if a response is taking too long or producing unwanted output.

### Clear Button

Clears all content from the OutputBox, giving you a clean slate for new interactions.

---

## Directives

DevMind supports four special directives that the LLM can use:

### FILE: / END_FILE — Create New Files

The LLM creates new files using this directive format:
```
FILE: <filename>
<raw source code>