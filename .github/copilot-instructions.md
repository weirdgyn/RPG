Repository safety rules

When modifying existing code:



NEVER replace an existing source file with an older version of that file.

NEVER restore, checkout, copy, or reconstruct a file from Git history unless I explicitly ask you to do so.

NEVER discard existing code in order to fix a local compilation error.

Make the smallest possible change required to solve the requested problem.

Preserve all unrelated existing code and behavior.

Before editing a file, treat the current working-tree version as the authoritative version.

Git history may be inspected to understand previous behavior, but historical versions must NEVER replace current files unless explicitly requested.

If a fix appears to require reverting unrelated changes, STOP and explain the conflict instead of performing the revert.

After modifying existing code, review the diff and verify that only changes required for the requested task were introduced.

Do not perform destructive Git operations such as reset, restore, checkout of files, force push, or history rewriting unless explicitly requested.



Before completing any modification to existing source code:



Inspect the resulting Git diff.

Check for unexpectedly deleted methods, properties, classes, or code blocks.

If the requested fix is local but the diff contains large unrelated changes, do not consider the task complete.

Revert the unrelated edits and keep only the minimal required change.







Bug fixing policy



For bug fixes:



Identify the smallest code region responsible for the problem.

Modify only that region unless additional changes are technically necessary.

Preserve existing methods, classes, features, comments, and configuration unrelated to the bug.

Do not use an older implementation merely because it compiles or previously worked.





