"use client";

import Box from "@mui/material/Box";
import AppBar from "@mui/material/AppBar";
import Toolbar from "@mui/material/Toolbar";
import Typography from "@mui/material/Typography";
import Drawer from "@mui/material/Drawer";
import List from "@mui/material/List";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemIcon from "@mui/material/ListItemIcon";
import ListItemText from "@mui/material/ListItemText";
import Divider from "@mui/material/Divider";
import ViewKanbanIcon from "@mui/icons-material/ViewKanban";
import SearchIcon from "@mui/icons-material/Search";
import AdminPanelSettingsIcon from "@mui/icons-material/AdminPanelSettings";
import LogoutIcon from "@mui/icons-material/Logout";
import Chip from "@mui/material/Chip";
import IconButton from "@mui/material/IconButton";
import Tooltip from "@mui/material/Tooltip";
import Link from "next/link";
import { usePathname } from "next/navigation";

import { useMe } from "@/lib/me";

const DRAWER_WIDTH = 220;

const NAV = [
  { href: "/", label: "Pipeline", icon: <ViewKanbanIcon /> },
  { href: "/search", label: "Loan Search", icon: <SearchIcon /> },
  { href: "/admin", label: "Admin", icon: <AdminPanelSettingsIcon />, staffOnly: true },
];

export default function AppShell({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const { me, isStaff } = useMe();

  return (
    <Box sx={{ display: "flex", minHeight: "100vh" }}>
      <AppBar
        position="fixed"
        elevation={0}
        sx={{
          zIndex: (t) => t.zIndex.drawer + 1,
          bgcolor: "background.paper",
          borderBottom: 1,
          borderColor: "divider",
        }}
      >
        <Toolbar variant="dense" sx={{ gap: 2 }}>
          <Typography
            variant="h6"
            component={Link}
            href="/"
            sx={{ color: "primary.main", textDecoration: "none", fontWeight: 700 }}
          >
            LOLA
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ flexGrow: 1 }}>
            Officer console
          </Typography>
          {me && (
            <Chip
              size="small"
              variant="outlined"
              label={`${me.name ?? me.sub} · ${me.roles.join(", ") || "no roles"}`}
            />
          )}
          <Tooltip title="Sign out">
            <IconButton size="small" component="a" href="/auth/logout">
              <LogoutIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Toolbar>
      </AppBar>

      <Drawer
        variant="permanent"
        sx={{
          width: DRAWER_WIDTH,
          flexShrink: 0,
          "& .MuiDrawer-paper": {
            width: DRAWER_WIDTH,
            boxSizing: "border-box",
            bgcolor: "background.paper",
            borderColor: "divider",
          },
        }}
      >
        <Toolbar variant="dense" />
        <Divider />
        <List dense sx={{ px: 1 }}>
          {NAV.filter((item) => !item.staffOnly || isStaff).map((item) => {
            const selected =
              item.href === "/"
                ? pathname === "/"
                : pathname.startsWith(item.href);
            return (
              <ListItemButton
                key={item.href}
                component={Link}
                href={item.href}
                selected={selected}
                sx={{
                  borderRadius: 1,
                  mb: 0.5,
                  "&.Mui-selected": { bgcolor: "action.selected" },
                }}
              >
                <ListItemIcon sx={{ minWidth: 36, color: "inherit" }}>
                  {item.icon}
                </ListItemIcon>
                <ListItemText primary={item.label} />
              </ListItemButton>
            );
          })}
        </List>
      </Drawer>

      <Box component="main" sx={{ flexGrow: 1, p: 3, minWidth: 0 }}>
        <Toolbar variant="dense" />
        {children}
      </Box>
    </Box>
  );
}
