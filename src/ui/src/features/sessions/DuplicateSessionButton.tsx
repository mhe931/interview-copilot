import * as AlertDialog from '@radix-ui/react-alert-dialog';
import { useEffect, useId, useRef, useState, type FormEvent } from 'react';
import { useNavigate } from 'react-router';
import { Button } from '@/components/ui/button';
import { Input, Label } from '@/components/ui/form';
import { useApp } from '@/lib/app-context';
import { MAX_SESSION_TITLE_LENGTH, type Session } from '@/types/api';

export function DuplicateSessionButton({ session }: { session: Session }) {
  const { api } = useApp();
  const navigate = useNavigate();
  const titleId = useId();
  const [open, setOpen] = useState(false);
  const [title, setTitle] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submitting = useRef(false);
  const errorSummary = useRef<HTMLParagraphElement>(null);

  useEffect(() => {
    if (error) errorSummary.current?.focus();
  }, [error]);

  async function submit(event: FormEvent) {
    event.preventDefault();
    if (submitting.current || !title.trim()) return;
    submitting.current = true;
    setBusy(true);
    setError(null);
    try {
      const copy = await api.createSession({
        title: title.trim(),
        mode: session.mode,
        answerLength: session.answerLength,
        framework: session.framework,
        pinnedQuestionType: session.pinnedQuestionType,
        sessionPrompt: session.sessionPrompt,
        copyFromSessionId: session.id,
      });
      setOpen(false);
      navigate(`/sessions/${copy.id}`);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not duplicate the session.');
    } finally {
      submitting.current = false;
      setBusy(false);
    }
  }

  return (
    <AlertDialog.Root open={open} onOpenChange={(next) => {
      if (submitting.current) return;
      if (next) {
        setTitle(`${session.title.slice(0, MAX_SESSION_TITLE_LENGTH - 7)} (copy)`);
        setError(null);
      }
      setOpen(next);
    }}>
      <AlertDialog.Trigger asChild>
        <Button variant="outline" size="sm" aria-label={`Duplicate ${session.title}`}>Duplicate</Button>
      </AlertDialog.Trigger>
      <AlertDialog.Portal>
        <AlertDialog.Overlay className="fixed inset-0 z-50 bg-black/50" />
        <AlertDialog.Content className="fixed left-1/2 top-1/2 z-50 grid w-[min(28rem,calc(100vw-2rem))] -translate-x-1/2 -translate-y-1/2 gap-4 rounded-[var(--radius-lg)] border border-border bg-background p-6 shadow-lg">
          <AlertDialog.Title className="text-lg font-semibold">Duplicate session</AlertDialog.Title>
          <AlertDialog.Description className="text-sm text-muted-foreground">
            Create a new draft with this session’s settings and documents.
          </AlertDialog.Description>
          <form onSubmit={(event) => void submit(event)} className="grid gap-4" aria-busy={busy}>
            <div className="grid gap-1.5">
              <Label htmlFor={titleId}>Title</Label>
              <Input id={titleId} value={title} onChange={(event) => setTitle(event.target.value)} required maxLength={MAX_SESSION_TITLE_LENGTH} disabled={busy} />
            </div>
            {error && <p ref={errorSummary} tabIndex={-1} role="alert" className="text-sm text-destructive">{error}</p>}
            <div className="flex justify-end gap-2">
              <AlertDialog.Cancel asChild><Button type="button" variant="outline" disabled={busy}>Cancel</Button></AlertDialog.Cancel>
              <Button type="submit" disabled={busy || !title.trim()}>{busy ? 'Duplicating…' : 'Duplicate'}</Button>
            </div>
          </form>
        </AlertDialog.Content>
      </AlertDialog.Portal>
    </AlertDialog.Root>
  );
}
