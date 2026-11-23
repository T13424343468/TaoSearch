#!/bin/bash
cd /root/workspace/taosearch-work
for i in $(seq 1 90); do
  sleep 60
  R1=$(gh run list --limit 5 2>/dev/null | grep -E "roleplay-accept|blind-test" | awk -F'\t' '{print $1"\t"$5"\t"$6}' | tr '\n' ' ')
  echo "[$i min] $R1" >> /root/workspace/taosearch-work/watch-rp.log
  DONE=$(gh run list --limit 5 2>/dev/null | grep -E "roleplay-accept|blind-test" | awk -F'\t' '$1=="completed"' | wc -l)
  TOTAL=$(gh run list --limit 5 2>/dev/null | grep -cE "roleplay-accept|blind-test")
  if [ "$DONE" -ge 2 ]; then echo "ALL_DONE" >> /root/workspace/taosearch-work/watch-rp.log; break; fi
done
