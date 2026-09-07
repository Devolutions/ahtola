namespace Ahtola.Core.Storage;

/// <summary>
/// Thrown when a bounded asynchronous b-tree traversal would need more resident
/// pages than its configured budget allows. The budget is an enforced ceiling,
/// not an advisory default: this is thrown instead of silently exceeding it.
/// </summary>
public sealed class SqliteBoundedPageBudgetExceededException(string message) : InvalidOperationException(message);
