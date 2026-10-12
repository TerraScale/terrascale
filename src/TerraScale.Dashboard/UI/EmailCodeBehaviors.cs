namespace TerraScale.Dashboard.UI;

// Preserve native editing and Datastar behavior from the established auth control.
public static class EmailCodeBehaviors
{
    public const string CodePointerDown = """
        if (evt.button !== 0 || !evt.isPrimary) return;
        evt.preventDefault();
        const anchor = el.selectionDirection === 'backward' ? el.selectionEnd : el.selectionStart;
        el.focus();
        const index = Math.min(el.value.length, Math.max(0, Math.min(5, Math.floor((evt.clientX - el.getBoundingClientRect().left) / (el.clientWidth / 6)))));
        el._codePointer = {id: evt.pointerId, index, anchor, shift: evt.shiftKey, x: evt.clientX};
        el.setPointerCapture(evt.pointerId);
        const start = evt.shiftKey ? Math.min(anchor, index) : index;
        const end = evt.shiftKey ? Math.max(anchor, Math.min(index + 1, el.value.length)) : Math.min(index + 1, el.value.length);
        el.setSelectionRange(start, end, evt.shiftKey && index < anchor ? 'backward' : 'forward');
        $_codeCaret = el.selectionStart; $_codeEnd = el.selectionEnd;
        """;

    public const string CodePointerMove = """
        const pointer = el._codePointer;
        if (!pointer || pointer.id !== evt.pointerId || (!pointer.dragging && Math.abs(evt.clientX - pointer.x) < 4)) return;
        evt.preventDefault();
        pointer.dragging = true;
        const index = Math.min(el.value.length, Math.max(0, Math.min(5, Math.floor((evt.clientX - el.getBoundingClientRect().left) / (el.clientWidth / 6)))));
        const anchor = pointer.shift ? pointer.anchor : pointer.index + (index < pointer.index ? 1 : 0);
        el.setSelectionRange(Math.min(anchor, index), Math.max(anchor, Math.min(index + 1, el.value.length)), index < anchor ? 'backward' : 'forward');
        $_codeCaret = el.selectionStart; $_codeEnd = el.selectionEnd;
        """;

    public const string CodePointerUp = """
        if (el._codePointer?.id === evt.pointerId) {
            el._codePointer = null;
            if (el.hasPointerCapture(evt.pointerId)) el.releasePointerCapture(evt.pointerId);
        }
        """;

    public const string CodeInput = """
        const digits = el.value.replace(/[^0-9]/g, '').slice(0, 6);
        if (digits !== el.value) {
            const start = el.value.slice(0, el.selectionStart).replace(/[^0-9]/g, '').length;
            const end = el.value.slice(0, el.selectionEnd).replace(/[^0-9]/g, '').length;
            el.value = digits;
            el.setSelectionRange(start, end);
        }
        $_emailCode = el.value; $_codeCaret = el.selectionStart; $_codeEnd = el.selectionEnd;
        """;

    public const string CodeBeforeInput = """
        if (evt.inputType !== 'insertText' || evt.isComposing || evt.data == null) return;
        const capacity = Math.max(0, 6 - el.value.length + el.selectionEnd - el.selectionStart);
        if (!/^[0-9]*$/.test(evt.data) || evt.data.length > capacity) evt.preventDefault();
        """;

    public const string CodePaste = """
        const text = evt.clipboardData?.getData('text');
        if (text == null) return;
        const capacity = Math.max(0, 6 - el.value.length + el.selectionEnd - el.selectionStart);
        if (/^[0-9]*$/.test(text) && text.length <= capacity) return;
        evt.preventDefault();
        const digits = text.replace(/[^0-9]/g, '').slice(0, capacity);
        if (digits) document.execCommand('insertText', false, digits);
        """;

}
