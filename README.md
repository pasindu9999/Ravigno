Same Idempotency-Key with a different body returns the original transfer unchanged
Replay reveals a transfer to anyone with the key: auth is out of scope

A dispatch retried after the transfer has been received returns 409.
The list has no paging.
The branchId filter matches From or To.
Invalid list filters return 400 and not an empty list.
