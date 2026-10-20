import { describe, expect, it } from 'vitest';
import { isSpokenEndCommand, SpokenEndInput } from './spokenControls';

describe('bounded spoken Lab end commands', () => {
  it.each(['ukonči test', 'Prosím, ukonči tento rozhovor.', 'vypni se', 'končíme', 'stop!',
    'můžeš prosím ukončit test?', 'please end this test', 'end the test', 'can you turn yourself off?', 'turn off'])
  ('recognizes a whole direct command: %s', value => { expect(isSpokenEndCommand(value)).toBe(true); });

  it.each(['neukončuj test', 'nechci ukončit test', "don't stop", 'do not end this test',
    'řekni ukonči test', 'kdybych řekl stop', 'if I say stop, what happens?', '„ukonči test“',
    'please say "stop"', 'stop znamená zastavit', 'ukonči test ale ještě ne', '', 'x'.repeat(161)])
  ('ignores negation, quotations, narrative and unrelated content: %s', value => { expect(isSpokenEndCommand(value)).toBe(false); });

  it('joins Live deltas, resets between input turns and rejects a truncated command prefix', () => {
    const input = new SpokenEndInput();
    input.append('ukon'); expect(input.matches()).toBe(false);
    input.append('či test'); expect(input.matches()).toBe(true);
    input.reset(); input.append(' rozhovor'); expect(input.matches()).toBe(false);
    input.reset(); input.append('stop'); input.append(' '.repeat(200)); expect(input.matches()).toBe(false);
  });
});
