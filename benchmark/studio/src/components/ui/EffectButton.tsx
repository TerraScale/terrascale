// Minimal action control for route states — the studio skeleton ships no UI
// kit; later tickets may replace this with the workspace component set.

export interface EffectButtonProps {
  readonly children: React.ReactNode;
  readonly onClick?: () => void;
  readonly asLink?: boolean;
  readonly href?: string;
}

export function EffectButton({ children, onClick, asLink = false, href }: EffectButtonProps) {
  const className =
    "rounded-panel border border-line bg-surface-raised px-3 py-1.5 text-sm text-ink transition-colors hover:border-accent hover:text-accent";
  if (asLink && href !== undefined) {
    return (
      <a href={href} className={className}>
        {children}
      </a>
    );
  }
  return (
    <button type="button" onClick={onClick} className={className}>
      {children}
    </button>
  );
}
