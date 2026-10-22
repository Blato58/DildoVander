"""Retired coordination entry point; performs no remote operations."""

import sys


def main():
    print('Coordination transport retired. Use connected Linear tools and '
          'docs/development-coordination.md for the selected task.', file=sys.stderr)
    return 2


if __name__ == '__main__':
    raise SystemExit(main())
