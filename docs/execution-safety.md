# Execution safety

## Revit API lane

All Revit API tools enter through `ExternalEventHandler`. The waiting queue is
bounded to 64 requests and one ExternalEvent callback processes at most eight
items before yielding back to Revit. Timed-out or client-cancelled queued items
are marked cancelled and skipped when dequeued.

Revit API tools must complete synchronously on the API thread. Returning an
incomplete task is treated as an execution-mode error. Cancellation is
cooperative: tools must check the supplied token before opening a transaction
and between batch items. A cancellation exception leaves an uncommitted Revit
transaction to roll back on disposal.

## Background lane

Tools implementing `IBackgroundMcpTool` have been audited not to access Revit.
They run in a separate serialized lane so two file/configuration mutations
cannot race each other. The marker
must only be added to tools that do not read, write, retain, or indirectly use
`UIApplication`, `Document`, or any other Autodesk API object.

## Approval binding

Model-related approvals capture:

- the exact in-process Revit document instance;
- a document change stamp maintained from `DocumentChanged` events;
- the selected element IDs when `useSelection=true`.

Approval is rejected if any captured context differs at execution time. Pending
approvals are capped at 64 and expire after ten minutes. Filesystem, Excel,
configuration, standards, and report tools in the background lane are not bound
to a Revit document.

## Transactions

Write tools check cancellation before opening transactions and between batch
items. `TransactionCommitGuard` converts a non-committed `Transaction.Commit`
status into an exception so the dispatcher reports a structured
`transaction_failed` result instead of reporting a false success.
