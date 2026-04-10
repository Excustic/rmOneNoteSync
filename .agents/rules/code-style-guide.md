---
trigger: always_on
---

# Project Code Style Guide & Agent Instructions

You are assisting with a modern .NET C# project. When writing, generating, or refactoring code, you MUST adhere to the following strict guidelines. Do not violate these rules.

### 1. 🚀 Modern C# Syntax (Zero CA Warnings)
Write code using the latest C# language features to avoid Code Analysis warnings. Examples:
* **Target-typed `new`:** Never repeat the type name if it can be inferred. 
  * *Bad:* `SemaphoreSlim lock = new SemaphoreSlim(5, 5);`
  * *Good:* `SemaphoreSlim lock = new(5, 5);`
* **Collection Expressions:** Use modern collection syntax instead of explicit instantiation.
  * *Bad:* `var list = new List<string> { "a", "b" };` or `Array.Empty<string>()`
  * *Good:* `List<string> list = ["a", "b"];` or `[]`
* Use **file-scoped namespaces** (`namespace Project.Name;`) rather than block-scoped namespaces (`namespace Project.Name { ... }`).

### 2. 🧱 Magic Numbers & Constants
Do not use "magic numbers" or hardcoded strings if they have intrinsic meaning or are reused.
* Extract them into `private const` or `public const` fields at the top of the class.
* Use `PascalCase` for constant names per standard C# conventions (e.g., `MaxRetryAttempts`, not `MAX_RETRY_ATTEMPTS`).

### 3. 💬 Comments & Professional Tone
Do not narrate the code. Comments should explain *why* something is done, not *what* is being done.
* **No First/Second Person:** Never use "We", "I", "You", or "You might" in comments. Write objectively in the third person or imperative mood.
  * *Bad:* `// We leave IsLoading = true to show the error, but you might want an Error state.`
  * *Good:* `// Leaves IsLoading true to persist the error message on the UI.`
* **Actionable Tags Only:** If there is a missing feature, a choice to be made, or technical debt, do not leave untrackable text comments. You MUST use standard tags like `TODO:` or `FIXME:`.
  * *Good:* `// TODO: Implement macOS support via .plist`

### 4. 📚 Documentation (XML Docstrings)
Provide concise XML `<summary>` docstrings for:
* All `public` methods and properties.
* Any private method that contains complex logic.
* Keep them brief and descriptive. Do not write massive paragraphs; just explain the method's primary responsibility and any critical edge cases.

### 5. 🛑 NO Backward Compatibility (Alpha Phase)
This project is currently in an Alpha state. Breaking changes are expected and welcome.
* **Do not write fallback logic** or legacy migration code for backward compatibility. 
* If a data structure changes, change it directly. Do not keep old enums (e.g., `InProgress = 99`) or write "fallback to memory" hacks for old versions.
* Assume the user is always on the latest clean build. If backward compatibility is ever needed in the future, the user will explicitly ask for it.