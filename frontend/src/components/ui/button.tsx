import * as React from 'react';
import { cn } from '@/lib/utils';

export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'default' | 'outline' | 'secondary' | 'ghost' | 'link';
  size?: 'default' | 'sm' | 'lg' | 'icon';
}

const Button = React.forwardRef<HTMLButtonElement, ButtonProps>(
  ({ className, variant = 'default', size = 'default', ...props }, ref) => {
    return (
      <button
        ref={ref}
        className={cn(
          'inline-flex items-center justify-center whitespace-nowrap font-medium transition-all focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-primary disabled:pointer-events-none disabled:opacity-50 select-none',
          // 规格要求：按钮圆角 8px
          'rounded-[8px]',
          variant === 'default' &&
            'bg-gradient-to-r from-blue-600 to-primary text-white shadow-md shadow-blue-500/20 hover:opacity-95 active:scale-[0.98]',
          variant === 'outline' &&
            'border border-white/10 bg-transparent hover:bg-white/5 text-slate-200',
          variant === 'secondary' &&
            'bg-slate-800 text-slate-100 hover:bg-slate-700/80',
          variant === 'ghost' &&
            'hover:bg-white/5 text-slate-300 hover:text-white',
          size === 'default' && 'h-10 px-4 py-2 text-sm',
          size === 'sm' && 'h-8 px-3 text-xs',
          size === 'lg' && 'h-12 px-6 text-base font-semibold',
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
