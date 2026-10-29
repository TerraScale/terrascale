package v1

import "fmt"

// applyWrite lets LocalDurable and RemoteDurable select the request guarantee.
// Other response-only durability values are rejected before sending a request.
func (d Durability) applyWrite(w *writeConfig) { w.durability = d }

func checkWriteDurability(d Durability) error {
	switch d {
	case "", LocalDurable, RemoteDurable:
		return nil
	}
	return fmt.Errorf("terrabase: request durability must be LocalDurable or RemoteDurable")
}

// WithDurability selects the transaction's acknowledgment guarantee. The default
// is LocalDurable; RemoteDurable waits for another placed region's durable
// acknowledgment (a one-machine cell may still answer LocalDurable). The server
// may refuse before execution or report ErrWriteOutcomeUnknown after execution;
// uncertain outcomes are never replayed automatically. Preconditions are kept.
func (t *Tx) WithDurability(d Durability) *Tx {
	if t.err == nil {
		t.err = checkWriteDurability(d)
	}
	t.durability = d
	return t
}
