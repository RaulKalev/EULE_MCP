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
approvals are capped at 64 and expire after ten minutes. A read-only tool's
temporary setup, always rolled back (the image export's crop and highlight), does
not count as a change. Filesystem, Excel,
configuration, standards, and report tools in the background lane are not bound
to a Revit document.

Deletes (`revit_delete_elements`, `revit_delete_views`, `revit_delete_sheets`) are
meant to wait for the user (#138). They read their targets by id again when they
run and skip what is gone, so they implement `IApprovalRevalidatesTargets`:

- other changes to the model do not invalidate them; the result carries a warning
  that the model changed in between;
- they stay valid for 24 hours;
- without a `document` argument they bind to the document active at the request, and
  still run there if the user has since switched to another project. A closed
  document still rejects them.

Direct Edit still does not bypass them.

## Transactions

Write tools check cancellation before opening transactions and between batch
items. `TransactionCommitGuard` converts a non-committed `Transaction.Commit`
status into an exception so the dispatcher reports a structured
`transaction_failed` result instead of reporting a false success.
