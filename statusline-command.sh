#!/bin/bash

input=$(cat)

RESET=$'\033[0m'
ORANGE=$'\033[38;2;232;101;10m'

# --- Claude plan usage ---
plan_part=""
usage_file="$HOME/.claude/usage_data.json"
if [ -f "$usage_file" ]; then
    plan_int=$(grep -o '"percent":[0-9]*'          "$usage_file" | grep -o '[0-9]*$')
    plan_used=$(grep -o '"used_dollars":[0-9.]*'   "$usage_file" | grep -o '[0-9.]*$')
    plan_total=$(grep -o '"total_dollars":[0-9.]*' "$usage_file" | grep -o '[0-9.]*$')

    if [ -n "$plan_int" ] && echo "$plan_int" | grep -qE '^[0-9]+$'; then
        plan_bar_width=20
        plan_filled=$(( plan_int * plan_bar_width / 100 ))
        [ $plan_filled -gt $plan_bar_width ] && plan_filled=$plan_bar_width
        plan_bar=""
        for i in $(seq 1 $plan_filled); do plan_bar="${plan_bar}█"; done
        plan_empty=$(( plan_bar_width - plan_filled ))
        for i in $(seq 1 $plan_empty); do plan_bar="${plan_bar}░"; done

        if echo "$plan_used" | grep -qE '^[0-9]+(\.[0-9]+)?$'; then
            plan_used_fmt=$(awk "BEGIN{printf \"%.2f\", $plan_used}")
            plan_total_fmt=$(awk "BEGIN{printf \"%.2f\", $plan_total}")
            plan_label="\$${plan_used_fmt} / \$${plan_total_fmt}"
        else
            plan_label="${plan_int}%"
        fi

        plan_part="${ORANGE}💰 ${plan_bar} ${plan_label}${RESET}"
    fi
fi

printf "%s\n" "$plan_part"
