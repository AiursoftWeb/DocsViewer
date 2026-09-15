(() => {
    'use strict';
    const form = document.getElementById('agent-form');
    if (!form) return;

    const input = form.querySelector('textarea');
    const send = document.getElementById('agent-send');
    const stop = document.getElementById('agent-cancel');
    const reset = document.getElementById('agent-new');
    const output = document.getElementById('agent-chat');
    const status = document.getElementById('agent-status');
    const configured = !send.disabled;
    const token = form.querySelector('input[name="__RequestVerificationToken"]').value;
    const headers = { 'Content-Type': 'application/json', RequestVerificationToken: token };
    let id = null;
    let generation = 0;
    let version = -1;
    let timer = null;
    let delay = 1000;
    let busy = false;

    function setBusy(value) {
        busy = value;
        send.disabled = value || !configured;
        stop.disabled = !value || !id;
    }

    function setStatus(message) {
        status.textContent = message || '';
    }

    function createActivity(events, open) {
        const accepted = (events || []).filter(event => event &&
            ['AssistantMessage', 'ToolCall', 'ToolExecution'].includes(event.Kind) &&
            ['Content', 'Proposed', 'Started', 'Succeeded', 'Failed', 'Deferred'].includes(event.Status)).slice(0, 20);
        if (accepted.length === 0) return null;

        const details = document.createElement('details');
        details.className = 'mt-3 small text-muted';
        details.open = open;
        const summary = document.createElement('summary');
        summary.textContent = open
            ? `${form.dataset.activityLive} (${accepted.length})`
            : `${form.dataset.activitySummary} (${accepted.length})`;
        details.append(summary);
        const list = document.createElement('ol');
        list.className = 'mb-0 mt-2 ps-3';

        for (const event of accepted) {
            const item = document.createElement('li');
            if (event.Kind === 'AssistantMessage') {
                item.textContent = `${form.dataset.activityAssistant}: ${event.Text || ''}`;
            } else if (event.Kind === 'ToolCall') {
                item.textContent = `${form.dataset.activityToolCall}: ${event.ToolName || ''}`;
                if (event.Arguments && typeof event.Arguments.query === 'string') {
                    const parameters = document.createElement('pre');
                    parameters.className = 'mb-0 mt-1 text-break';
                    parameters.textContent = JSON.stringify({ query: event.Arguments.query }, null, 2);
                    item.append(parameters);
                }
            } else if (event.Status === 'Started') {
                item.textContent = form.dataset.activityToolStarted;
            } else if (event.Status === 'Succeeded') {
                item.textContent = form.dataset.activityToolSucceeded;
                if (Number.isInteger(event.ResultCount)) {
                    const summary = document.createElement('div');
                    summary.textContent = `${form.dataset.activityResultCount}: ${event.ResultCount}`;
                    item.append(summary);
                }
                for (const citation of event.Citations || []) {
                    const source = document.createElement('div');
                    source.textContent = `${citation.Label || ''} ${citation.Title || ''}`.trim();
                    item.append(source);
                }
            } else if (event.Status === 'Deferred') {
                item.textContent = form.dataset.activityToolDeferred;
            } else {
                item.textContent = form.dataset.activityToolFailed;
            }
            list.append(item);
        }

        details.append(list);
        return details;
    }

    function render(messages, activeEvents) {
        output.replaceChildren();
        for (const message of messages || []) {
            const block = document.createElement('section');
            block.className = 'border rounded p-3 mb-3 text-break';
            const content = document.createElement('div');
            content.style.whiteSpace = 'pre-wrap';
            content.textContent = message.Content;
            if (message.Role === 'user') block.classList.add('bg-body-tertiary');
            block.append(content);

            for (const citation of message.Citations || []) {
                let url;
                try { url = new URL(citation.Url, location.origin); }
                catch { continue; }
                if (url.origin !== location.origin || !['http:', 'https:'].includes(url.protocol)) continue;
                const item = document.createElement('div');
                const link = document.createElement('a');
                link.href = url.href;
                link.textContent = `${citation.Label} ${citation.Title}`;
                item.append(link);
                block.append(item);
            }

            const activity = createActivity(message.ProcessEvents, false);
            if (activity) block.append(activity);
            output.append(block);
        }

        const active = createActivity(activeEvents, true);
        if (active) {
            const block = document.createElement('section');
            block.className = 'border rounded p-3 mb-3 text-break bg-body-tertiary';
            block.append(active);
            output.append(block);
        }
    }

    function schedule(currentGeneration) {
        clearTimeout(timer);
        timer = setTimeout(() => poll(currentGeneration), delay);
    }

    async function poll(currentGeneration) {
        if (currentGeneration !== generation || !id) return;
        try {
            const response = await fetch(`${form.dataset.status}?conversationId=${encodeURIComponent(id)}`, { cache: 'no-store' });
            if (currentGeneration !== generation) return;
            if ([401, 403, 404].includes(response.status) || response.redirected) {
                setStatus(form.dataset.lost);
                setBusy(false);
                return;
            }
            if (!response.ok) throw new Error('status');
            const state = await response.json();
            if (currentGeneration !== generation || state.Version < version) return;
            if (state.Version >= version) {
                version = state.Version;
                render(state.Messages, state.ActiveProcessEvents);
                setStatus(state.ErrorMessage || ({
                    Thinking: form.dataset.thinking,
                    Cancelling: form.dataset.cancelling,
                    Cancelled: form.dataset.cancelled,
                    Completed: form.dataset.completed
                })[state.State] || form.dataset.error);
            }
            if (['Completed', 'Error', 'Cancelled'].includes(state.State)) {
                setBusy(false);
                return;
            }
            delay = 1500;
            schedule(currentGeneration);
        } catch {
            if (currentGeneration === generation) {
                setStatus(form.dataset.retrying);
                delay = Math.min(delay * 2, 15000);
                schedule(currentGeneration);
            }
        }
    }

    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (busy || !form.reportValidity()) return;
        const currentGeneration = ++generation;
        setBusy(true);
        setStatus(form.dataset.thinking);
        reset.disabled = true;
        try {
            const response = await fetch(form.dataset.send, {
                method: 'POST',
                headers,
                body: JSON.stringify({ Message: input.value, ConversationId: id })
            });
            if (currentGeneration !== generation) return;
            if (response.redirected) throw new Error('authentication');
            const result = await response.json();
            if (!response.ok) {
                setStatus(result.ErrorMessage || form.dataset.error);
                setBusy(false);
                return;
            }
            id = result.ConversationId;
            input.value = '';
            stop.disabled = false;
            delay = 1000;
            await poll(currentGeneration);
        } catch {
            if (currentGeneration === generation) {
                setStatus(form.dataset.error);
                setBusy(false);
            }
        } finally {
            reset.disabled = false;
        }
    });

    stop.addEventListener('click', async () => {
        if (!id || !busy) return;
        stop.disabled = true;
        const currentGeneration = generation;
        try {
            const response = await fetch(`${form.dataset.cancel}?conversationId=${encodeURIComponent(id)}`, {
                method: 'POST',
                headers
            });
            if (currentGeneration !== generation) return;
            if (!response.ok || response.redirected) throw new Error('cancel');
            setStatus(form.dataset.cancelling);
            schedule(currentGeneration);
        } catch {
            if (currentGeneration === generation) {
                setStatus(form.dataset.error);
                stop.disabled = false;
            }
        }
    });

    reset.addEventListener('click', async () => {
        if (busy) {
            stop.click();
            return;
        }
        generation++;
        clearTimeout(timer);
        id = null;
        version = -1;
        output.replaceChildren();
        setStatus('');
        input.value = '';
        setBusy(false);
        input.focus();
    });
})();
