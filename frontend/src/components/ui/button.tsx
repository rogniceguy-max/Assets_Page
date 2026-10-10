import * as React from 'react';
import { cn } from '@/lib/utils';

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'default' | 'outline' | 'secondary' | 'ghost' | 'destructive' | 'link';
  size?: 'default' | 'sm' | 'lg' | 'icon';
}

const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant = 'default', size = 'default', ...props }, ref) => {
    return (
      <button
        ref={ref}
        className={cn(
          'inline-flex items-center justify-center whitespace-nowrap font-medium transition-colors duration-150',
          'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent focus-visible:ring-offset-2 focus-visible:ring-offset-background',
          'disabled:pointer-events-none disabled:opacity-40 select-none rounded-[8px]',
          // 规格要求：主按钮使用香槟铜金，无彩色光晕投影，单一视觉重点
          variant === 'default' &&
            'bg-accent text-accent-foreground hover:bg-accent-hover active:bg-accent-active font-semibold shadow-sm',
          variant === 'outline' &&
            'border border-border bg-transparent hover:bg-secondary/60 text-foreground',
          variant === 'secondary' &&
            'bg-secondary text-foreground hover:bg-secondary-hover border border-border',
          variant === 'ghost' &&
            'hover:bg-secondary/60 text-muted hover:text-foreground',
          variant === 'destructive' &&
            'bg-destructive-bg text-destructive border border-destructive-border hover:bg-destructive-bg/80',
          variant === 'link' &&
            'text-accent underline-offset-4 hover:underline p-0 h-auto',
          size === 'default' && 'h-10 px-4 py-2 text-sm',
          size === 'sm' && 'h-8 px-3 text-xs',
          size === 'lg' && 'h-11 px-5 text-sm font-semibold',
          size === 'icon' && 'h-9 w-9 p-0',
          className
        )}
        {...props}
      />
    );
  }
);
Button.displayName = 'Button';

export { Button };
